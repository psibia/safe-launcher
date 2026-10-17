"""Тестовое «приложение»: по кругу поиск имени, приветствие по HTTP, скачивание, загрузка и эхо по UDP.

Маршрут ядро выбирает по пути процесса, поэтому у каждого приложения своя копия интерпретатора.
"""
import argparse
import errno
import hashlib
import http.client
import json
import os
import random
import socket
import time


def one_round(args):
    result = {"start": round(time.time(), 3)}
    try:
        started = time.time()
        address = socket.getaddrinfo(args.host, 80, socket.AF_INET, socket.SOCK_STREAM)[0][4][0]
        result["ip"] = address
        result["dns_s"] = round(time.time() - started, 3)

        connection = http.client.HTTPConnection(args.host, 80, timeout=60)
        connection.request("GET", "/")
        hello = connection.getresponse().read().decode()
        result["hello"] = hello.strip()
        result["hello_ok"] = args.expect in hello and not (args.forbid and args.forbid in hello)

        started = time.time()
        connection.request("GET", f"/bytes?n={args.dl}")
        response = connection.getresponse()
        want = response.getheader("X-Sum")
        digest = hashlib.sha256()
        size = 0
        while True:
            chunk = response.read(1 << 16)
            if not chunk:
                break
            digest.update(chunk)
            size += len(chunk)
        result["download_ok"] = size == args.dl and digest.hexdigest() == want
        result["download_s"] = round(time.time() - started, 3)

        data = os.urandom(args.ul)
        started = time.time()
        connection.request("POST", "/upload", body=data, headers={"Content-Type": "application/octet-stream"})
        answer = connection.getresponse().read().decode().split()
        result["upload_ok"] = answer == [str(args.ul), hashlib.sha256(data).hexdigest()]
        result["upload_s"] = round(time.time() - started, 3)
        connection.close()

        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.settimeout(2)
        sock.connect((address, 7))
        echoed = 0
        for _ in range(args.udp):
            payload = os.urandom(random.randint(16, 1300))
            for _ in range(3):
                try:
                    sock.send(payload)
                    while sock.recv(65535) != payload:
                        pass
                    echoed += 1
                    break
                except socket.timeout:
                    continue
                except OSError as error:
                    # MTU по пути меньше (ICMP «fragmentation needed»): следующую попытку ядро Linux разобьёт на части
                    if error.errno != errno.EMSGSIZE:
                        raise
                    result["udp_emsgsize"] = result.get("udp_emsgsize", 0) + 1
        sock.close()
        result["udp_ok"] = echoed == args.udp
        result["udp_echoed"] = echoed
    except Exception as error:
        result["error"] = repr(error)
    result["ok"] = all(result.get(key) for key in ("hello_ok", "download_ok", "upload_ok", "udp_ok"))
    result["end"] = round(time.time(), 3)
    return result


def read_response(sock, buffer):
    """Один ответ HTTP/1.1 с Content-Length из постоянного соединения."""
    while b"\r\n\r\n" not in buffer:
        chunk = sock.recv(65536)
        if not chunk:
            raise ConnectionError("соединение закрыто другой стороной")
        buffer += chunk
    head, _, rest = buffer.partition(b"\r\n\r\n")
    length = 0
    for line in head.split(b"\r\n")[1:]:
        key, _, value = line.partition(b":")
        if key.strip().lower() == b"content-length":
            length = int(value)
    while len(rest) < length:
        chunk = sock.recv(65536)
        if not chunk:
            raise ConnectionError("соединение закрыто другой стороной")
        rest += chunk
    return rest[:length].decode(errors="replace"), rest[length:]


def hold(args):
    """Одно соединение TCP и один поток UDP на всё время, обращение каждые 0,3 с. Переподключений нет: оборванное
    соединение видно как ошибка, оборванный поток UDP как долгий перерыв без эха."""
    result = {"start": round(time.time(), 3), "tcp_requests": 0, "udp_echoes": 0, "udp_lost": 0, "errors": []}
    end = time.time() + args.hold
    try:
        address = socket.getaddrinfo(args.host, 80, socket.AF_INET, socket.SOCK_STREAM)[0][4][0]
        result["ip"] = address
        tcp = socket.create_connection((address, 80), timeout=10)
        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        udp.settimeout(1)
        udp.connect((address, 7))
    except Exception as error:
        result["errors"].append({"t": round(time.time() - result["start"], 1), "what": "open", "error": repr(error)})
        tcp = udp = None
    buffer = b""
    last_echo = time.time()
    gap = 0.0
    while tcp is not None and time.time() < end:
        if tcp is not None:
            try:
                tcp.sendall(f"GET / HTTP/1.1\r\nHost: {args.host}\r\n\r\n".encode())
                body, buffer = read_response(tcp, buffer)
                if args.expect not in body or (args.forbid and args.forbid in body):
                    raise ValueError("неожиданный ответ: " + body.strip()[:120])
                result["tcp_requests"] += 1
            except Exception as error:
                result["errors"].append({"t": round(time.time() - result["start"], 1), "what": "tcp", "error": repr(error)})
                tcp.close()
                break
        payload = os.urandom(random.randint(16, 1200))
        try:
            udp.send(payload)
            while udp.recv(65535) != payload:
                pass
            result["udp_echoes"] += 1
            last_echo = time.time()
        except (socket.timeout, OSError):
            result["udp_lost"] += 1
        gap = max(gap, time.time() - last_echo)
        time.sleep(0.3)
    result["udp_worst_gap_s"] = round(gap, 1)
    result["ok"] = not result["errors"] and result["tcp_requests"] > 0 and result["udp_echoes"] > 0 and gap < 4
    result["end"] = round(time.time(), 3)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--name", required=True)
    parser.add_argument("--host", required=True)
    parser.add_argument("--expect", required=True)
    parser.add_argument("--forbid", default="")
    parser.add_argument("--rounds", type=int, default=1)
    parser.add_argument("--dl", type=int, default=16 << 20)
    parser.add_argument("--ul", type=int, default=8 << 20)
    parser.add_argument("--udp", type=int, default=20)
    parser.add_argument("--pause", type=float, default=0)
    parser.add_argument("--out", required=True)
    parser.add_argument("--hold", type=float, default=0)
    args = parser.parse_args()
    if args.hold:
        with open(args.out, "w") as file:
            json.dump({"name": args.name, "rounds": [hold(args)]}, file)
        return
    rounds = []
    for _ in range(args.rounds):
        rounds.append(one_round(args))
        with open(args.out, "w") as file:
            json.dump({"name": args.name, "rounds": rounds}, file)
        if not rounds[-1]["ok"] and args.pause:
            time.sleep(args.pause)


main()
