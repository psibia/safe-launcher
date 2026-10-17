"""Сценарии стенда ядра VPN лаунчера (запускает bench.sh, описание в README.md).

Одно ядро с настройками, как их строит служба лаунчера (адаптер TUN, маршруты по процессам, DNS каждого приложения через
его подключение), ведёт несколько приложений сразу, каждое через своё подключение: AmneziaWG 3.1 (amneziawg-go
v3.1.20260828), обычный WireGuard, AmneziaWG 1.0 (amneziawg-go v0.2.12) и VLESS, а программа вне лаунчера идёт напрямую.
Все они передают данные одновременно, при перезапусках ядра и при разном порядке подключений.

Запуск (root): python3 -I run.py <ядро sing-box> [шаг ...] или python3 -I run.py --list
"""
import base64
import itertools
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
import uuid

SCRIPTS = os.path.dirname(os.path.abspath(__file__))
ROOT = os.environ.get("CORE_BENCH_WORK", "/tmp/core-bench")
LIST_ONLY = sys.argv[1:2] == ["--list"]
SINGBOX = None if LIST_ONLY else os.path.abspath(sys.argv[1])
STEPS = [] if LIST_ONLY else sys.argv[2:]
BIN = os.path.join(ROOT, "bin")
APPS = os.path.join(ROOT, "apps")
V31 = "v3.1.20260828"
WORK = os.path.join(ROOT, "run-" + time.strftime("%Y%m%d-%H%M%S"))
PYTHON = os.path.realpath(sys.executable)
PROBE = os.path.join(ROOT, "probe", "Probe.dll")
CFGCHECK = os.path.join(ROOT, "cfgcheck", "CfgCheck.dll")
DOTNET = shutil.which("dotnet")
processes = []


def run(*command, check=True):
    return subprocess.run(command, check=check, capture_output=True, text=True)


def netns(name, *command):
    return ["ip", "netns", "exec", name, *command]


def start(name, command):
    log = open(os.path.join(WORK, name + ".log"), "a")
    process = subprocess.Popen(command, stdout=log, stderr=subprocess.STDOUT)
    processes.append(process)
    return process


def keypair():
    out = subprocess.check_output([SINGBOX, "generate", "wg-keypair"], text=True)
    values = dict(line.split(": ", 1) for line in out.strip().splitlines())
    return values["PrivateKey"], values["PublicKey"]


def b64hex(value):
    return base64.b64decode(value).hex()


def setup_network():
    """Два сетевых пространства: srv (серверы) и cli (ядро и приложения), между ними пара veth. Адреса из диапазонов
    для документации и частных сетей: стенд никуда не ходит."""
    for name in ("srv", "cli"):
        run("ip", "netns", "del", name, check=False)
    run("ip", "netns", "add", "srv")
    run("ip", "netns", "add", "cli")
    run("ip", "link", "add", "vc", "netns", "cli", "type", "veth", "peer", "name", "vs", "netns", "srv")
    run("ip", "-n", "srv", "addr", "add", "10.99.0.1/24", "dev", "vs")
    run("ip", "-n", "srv", "addr", "add", "203.0.113.10/32", "dev", "lo")
    run("ip", "-n", "cli", "addr", "add", "10.99.0.2/24", "dev", "vc")
    for name, link in (("srv", "lo"), ("srv", "vs"), ("cli", "lo"), ("cli", "vc")):
        run("ip", "-n", name, "link", "set", link, "up")
    run("ip", "-n", "cli", "route", "add", "default", "via", "10.99.0.1")
    os.makedirs("/etc/netns/cli", exist_ok=True)
    with open("/etc/netns/cli/resolv.conf", "w") as file:
        file.write("nameserver 198.51.100.53\noptions timeout:3 attempts:3\n")


HP_KEY = base64.b64encode(os.urandom(32)).decode()
VLESS_UUID = str(uuid.uuid4())

# подключение: сервер (версия amneziawg-go, имя в ответе, порт, строки AmneziaWG), у клиента параметры amnezia,
# дополнительные поля peer и DNS из файла подключения
SERVERS = {
    "A": (V31, "awg3.1", 51821,
          "jc=3\njmin=20\njmax=300\ns1=24\ns2=12\ns3=16\ns4=13\n"
          "h1=100000-200000\nh2=300000-400000\nh3=500000-600000\nh4=700000-800000\n"
          "header_protection_key=" + b64hex(HP_KEY) + "\ncontent_padding_addition=0-32\nrandom_trailers=true"),
    "B": ("v0.2.19", "plain-wg", 51822, ""),
    "C": ("v0.2.12", "awg1.0-v0.2.12", 51823,
          "jc=4\njmin=40\njmax=70\ns1=15\ns2=40\nh1=1020325451\nh2=3288052141\nh3=1766607858\nh4=2528465083"),
    "E": ("v0.2.19", "plain-wg-2", 51824, ""),
}
AMNEZIA = {
    "A": {"jc": 6, "jmin": 10, "jmax": 1000, "s1": 24, "s2": 12, "s3": 16, "s4": 13,
          "h1": "100000-200000", "h2": "300000-400000", "h3": "500000-600000", "h4": "700000-800000",
          "i1": "<b 0xc70000000108><r 30><t>", "itime": 30, "header_protection_key": HP_KEY,
          "content_padding_addition": "0-64", "rekey_after_time": "30-40", "rekey_timeout": "4-6",
          "reject_after_time": "170-180", "keepalive_timeout": "8-12", "max_handshake_attempts": "10-20",
          "random_trailers": True},
    "C": {"jc": 4, "jmin": 40, "jmax": 70, "s1": 15, "s2": 40,
          "h1": "1020325451", "h2": "3288052141", "h3": "1766607858", "h4": "2528465083"},
}
PEER_EXTRA = {"A": {"persistent_keepalive_range": "20-30"}, "B": {"persistent_keepalive_interval": 25}}
FILE_DNS = {"A": "10.66.0.1", "C": "10.66.0.1"}
EXPECT = {"A": ("hello from awg3.1", ""), "B": ("hello from plain-wg,", ""), "C": ("hello from awg1.0-v0.2.12", ""),
          "E": ("hello from plain-wg-2,", ""),
          "D": ("hello from direct-server", "you are 10.99.0.2:")}
CONNECTIONS = {}


def setup_servers():
    for app, (version, label, port, lines) in SERVERS.items():
        server_private, server_public = keypair()
        client_private, client_public = keypair()
        uapi = ["private_key=" + b64hex(server_private), f"listen_port={port}"]
        uapi += [line for line in lines.split("\n") if line]
        uapi += ["public_key=" + b64hex(client_public), "allowed_ip=10.66.0.2/32"]
        path = os.path.join(WORK, f"server-{app}.uapi")
        with open(path, "w") as file:
            file.write("\n".join(uapi) + "\n")
        start(f"server-{app}", netns("srv", os.path.join(BIN, "awgserver-" + version), "-uapi", path, "-label", label))
        peer = {"address": "10.99.0.1", "port": port, "public_key": server_public, "allowed_ips": ["0.0.0.0/0"]}
        peer.update(PEER_EXTRA.get(app, {}))
        endpoint = {"type": "wireguard", "address": ["10.66.0.2/32"], "private_key": client_private, "peers": [peer]}
        if app == "C":
            endpoint["mtu"] = 1280
        if app in AMNEZIA:
            endpoint["amnezia"] = AMNEZIA[app]
        CONNECTIONS[app] = endpoint
    CONNECTIONS["D"] = {"type": "vless", "server": "10.99.0.1", "server_port": 8443, "uuid": VLESS_UUID}

    vless_server = {
        "log": {"level": "info", "timestamp": True},
        "dns": {"servers": [{"type": "hosts", "tag": "hosts", "predefined": {"site.test": "203.0.113.10"}}],
                "rules": [{"query_type": ["TXT"], "action": "predefined", "rcode": "NOERROR",
                           "answer": ['site.test. 60 IN TXT "via-vless"']}],
                "final": "hosts"},
        "inbounds": [{"type": "vless", "tag": "vless-in", "listen": "10.99.0.1", "listen_port": 8443,
                      "users": [{"name": "user", "uuid": VLESS_UUID}]}],
        "outbounds": [{"type": "direct", "tag": "direct"}],
        "route": {"rules": [{"action": "sniff"}, {"protocol": ["dns"], "action": "hijack-dns"},
                            {"port": [53], "action": "hijack-dns"}],
                  "final": "direct", "default_domain_resolver": "hosts"},
    }
    path = os.path.join(WORK, "vless-server.json")
    with open(path, "w") as file:
        json.dump(vless_server, file, indent=1)
    start("vless-server", netns("srv", SINGBOX, "run", "-c", path))
    start("http-server", netns("srv", PYTHON, "-I", os.path.join(SCRIPTS, "httpsrv.py")))


def app_match(app):
    # служба лаунчера выбирает приложение выражением «^launcher:<метка>\|» (правка 0001 так называет процессы, запущенные
    # из лаунчера, только в Windows); здесь приложения различаются по папке их программы
    return "^" + re.escape(os.path.join(APPS, app) + "/")


def go_quote(text):
    """regexp.QuoteMeta из Go (CoreConfigBuilder.GoQuote)."""
    return "".join("\\" + c if c in "\\.+*?()|[]{}^$" else c for c in text)


def outside_pattern(folder, name, subfolders):
    """CoreConfigBuilder.OutsidePatterns службы, с «/» вместо «\\»: программа приложения в его папке и, с subfolders,
    на любой глубине под ней; регистр букв не важен."""
    return "(?i)^" + go_quote(folder + "/") + ("(?:[^/]+/)*" if subfolders else "") + go_quote(name) + "$"


def build_config(plan, hide_dns=False, api=False, dns_via=None, check=False, outside=None, block_outside=False):
    """Как CoreConfigBuilder.Build службы, для приложений plan[i], у каждого своё подключение. При изменении правил
    в CoreConfigBuilder их нужно повторить здесь (сами настройки службы проверяет шаг service-config).
    dns_via: подключение для DNS-запросов, на которые ядро не отвечает подменой (CoreRequest.DnsOutbound);
    check: прокси для проверки подключений через работающее ядро (CoreApi.CheckPort);
    outside: шаблоны программ приложения для его процессов без метки лаунчера (CoreAppRoute.Programs), через его
    подключение или, с block_outside, без сети."""
    outside = outside or {}
    outbounds = [{"type": "direct", "tag": "direct"}]
    endpoints = []
    dns_of = {}
    for app in plan + ([dns_via] if dns_via and dns_via not in plan else []):
        connection = dict(CONNECTIONS[app], tag="c" + app)
        (endpoints if connection["type"] == "wireguard" else outbounds).append(connection)
        if app in FILE_DNS:
            dns_of["c" + app] = FILE_DNS[app]
    rules = []
    check_inbound = None
    if check:
        tags = [o["tag"] for o in outbounds[1:] + endpoints]
        check_inbound = {"type": "mixed", "tag": "check-in", "listen": "127.0.0.1", "listen_port": CHECK,
                         "users": [{"username": tag, "password": "bench"} for tag in tags]}
        rules += [{"inbound": ["check-in"], "auth_user": [tag], "action": "route", "outbound": tag} for tag in tags]
        rules.append({"inbound": ["check-in"], "action": "reject"})
    dns_servers = [{"type": "local", "tag": "local"}]
    dns_rules = []
    if plan:
        sniffed = ["^" + re.escape(APPS + "/")]
        if not block_outside:
            sniffed += [p for app in plan for p in outside.get(app, [])]
        rules.append({"process_path_regex": sniffed, "action": "sniff", "override_destination": True})
    if hide_dns:
        rules.append({"port": [53], "action": "sniff", "sniffer": ["dns"]})
    rules.append({"protocol": ["dns"], "action": "hijack-dns"})
    for app in plan:
        outbound = "c" + app
        own = dns_of.get(outbound)
        dns_servers.append({"type": "tcp" if own is None else "udp", "tag": "dns-" + outbound,
                            "server": own or "1.1.1.1", "detour": outbound})
        same = [] if block_outside else outside.get(app, [])
        dns_rules.append({"process_path_regex": [app_match(app)] + same, "action": "route", "server": "dns-" + outbound})
        rules.append({"process_path_regex": [app_match(app)] + same, "action": "route", "outbound": outbound})
        if block_outside and outside.get(app):
            rules.append({"process_path_regex": outside[app], "action": "reject"})
    if hide_dns:
        dns_servers.append({"type": "fakeip", "tag": "fakeip", "inet4_range": "198.18.0.0/15"})
        dns_rules.append({"domain_suffix": ["msftncsi.com", "msftconnecttest.com", "local", "localhost", "arpa"],
                          "domain_regex": ["^[^.]+$"], "action": "route", "server": "local"})
        dns_rules.append({"protocol": ["dns"], "query_type": ["HTTPS", "SVCB"], "action": "predefined", "rcode": "NOERROR"})
        dns_rules.append({"protocol": ["dns"], "query_type": ["A", "AAAA"], "action": "route", "server": "fakeip",
                          "rewrite_ttl": 1})
        if dns_via:
            outbound = "c" + dns_via
            if not any(s["tag"] == "dns-" + outbound for s in dns_servers):
                own = dns_of.get(outbound)
                dns_servers.append({"type": "tcp" if own is None else "udp", "tag": "dns-" + outbound,
                                    "server": own or "1.1.1.1", "detour": outbound})
            dns_rules.append({"protocol": ["dns"], "action": "route", "server": "dns-" + outbound})
    config = {
        "log": {"level": "debug", "timestamp": True},
        "inbounds": [{"type": "tun", "tag": "tun-in", "interface_name": "SafeLauncher", "address": ["172.31.250.1/30"],
                      # служба ставит и strict_route; здесь он выключен: строгим маршрутам нужен IPv6, а его может не быть
                      "auto_route": True, "strict_route": False, "stack": "mixed"}],
        "dns": {"servers": dns_servers, "rules": dns_rules, "final": "local"},
        "outbounds": outbounds,
        "route": {"auto_detect_interface": True, "default_domain_resolver": "local", "find_process": True,
                  "rules": rules, "final": "direct"},
    }
    if endpoints:
        config["endpoints"] = endpoints
    if check_inbound:
        config["inbounds"].append(check_inbound)
    if api:
        config["experimental"] = {"clash_api": {"external_controller": API, "secret": "bench"}}
    return config


core = None
core_log = None
API = "127.0.0.1:9095"
CHECK = 9096


def api_connections():
    """GET /connections у clash API ядра (лаунчер читает его так же): список или None."""
    code = ("import urllib.request as u; r = u.Request('http://%s/connections', headers={'Authorization': 'Bearer bench'}); "
            "print(u.urlopen(r, timeout=5).read().decode())" % API)
    out = run(*netns("cli", PYTHON, "-I", "-c", code), check=False)
    try:
        return json.loads(out.stdout).get("connections") or []
    except ValueError:
        print("  api: " + (out.stdout + out.stderr)[-300:])
        return None


def describe_connection(c):
    m = c["metadata"]
    return (f"{m['network']} {m['type']} {m['destinationIP']}:{m['destinationPort']} host={m['host']!r} "
            f"proc={os.path.basename(m['processPath'])!r} chains={c['chains']} rule={c['rule'][:70]!r} "
            f"up={c['upload']} down={c['download']}")


def start_core(name, plan, hide_dns=False, api=False, dns_via=None, check=False, outside=None, block_outside=False):
    global core, core_log
    stop_core()
    path = os.path.join(WORK, f"core-{name}.json")
    with open(path, "w") as file:
        json.dump(build_config(plan, hide_dns, api, dns_via, check, outside, block_outside), file, indent=1)
    core_log = os.path.join(WORK, f"core-{name}.log")
    core = subprocess.Popen(netns("cli", SINGBOX, "run", "-c", path), stdout=open(core_log, "w"), stderr=subprocess.STDOUT)
    deadline = time.time() + 20
    while time.time() < deadline:
        if "sing-box started" in open(core_log).read():
            return
        if core.poll() is not None:
            break
        time.sleep(0.1)
    raise SystemExit(f"ядро не запустилось, см. {core_log}:\n" + open(core_log).read()[-2000:])


def stop_core():
    global core
    if core is None:
        return
    core.send_signal(signal.SIGTERM)
    try:
        core.wait(15)
    except subprocess.TimeoutExpired:
        core.kill()
        core.wait()
        print("  ядро не остановилось за 15 с")
    core = None


def interpreter(app):
    """Своя копия Python для каждого приложения: ядро различает приложения по пути программы (ссылка не годится,
    ядро видит путь настоящего файла)."""
    if app == "direct":
        return PYTHON
    path = os.path.join(APPS, app, "app")
    if not os.path.exists(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        shutil.copy(PYTHON, path)
    return path


def launch_apps(step, apps, rounds, **options):
    running = {}
    for app in apps:
        if app == "direct":
            host, expect, forbid = "10.99.0.1", "you are 10.99.0.2:", ""
        else:
            host = "site.test"
            expect, forbid = EXPECT[app]
        out = os.path.join(WORK, f"{step}-{app}.json")
        command = netns("cli", interpreter(app), "-I", os.path.join(SCRIPTS, "app.py"), "--name", app, "--host", host,
                        "--expect", expect, "--forbid", forbid, "--rounds", str(rounds), "--out", out)
        for key, value in options.items():
            command += ["--" + key, str(value)]
        running[app] = (subprocess.Popen(command, stdout=open(out + ".log", "w"), stderr=subprocess.STDOUT), out)
    return running


def collect(running, timeout=900):
    results = {}
    deadline = time.time() + timeout
    for app, (process, out) in running.items():
        try:
            process.wait(max(1, deadline - time.time()))
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        try:
            results[app] = json.load(open(out))["rounds"]
        except (OSError, ValueError):
            results[app] = [{"ok": False, "error": "нет результата: " + open(out + ".log").read()[-500:]}]
    return results


def summary(results, last_only=False):
    lines = []
    ok = True
    for app, rounds in results.items():
        checked = rounds[-1:] if last_only else rounds
        app_ok = bool(checked) and all(r.get("ok") for r in checked)
        ok &= app_ok
        speeds = [f"{16 / r['download_s']:.0f}" for r in rounds if r.get("download_s")]
        bad = [r for r in checked if not r.get("ok")]
        detail = f"раундов {sum(r.get('ok', False) for r in rounds)}/{len(rounds)}, загрузка МБ/с {'/'.join(speeds)}"
        if bad:
            detail += f"; не прошло: {json.dumps(bad[0], ensure_ascii=False)[:400]}"
        lines.append(f"    {'ok ' if app_ok else 'BAD'} {app}: {detail}")
    return ok, "\n".join(lines)


def core_health():
    alive = core is not None and core.poll() is None
    text = open(core_log).read() if core_log else ""
    panic = "panic" in text.lower() and "goroutine" in text
    return alive and not panic, f"ядро работает={alive}, panic={panic}"


def step_concurrent(name, plan, rounds=3, hide_dns=False, first_alone=None, dns_via=None):
    """Приложения plan и программа вне лаунчера передают данные одновременно; с first_alone сначала одни эти приложения,
    потом ядро перезапускается со всеми (новое подключение в настройках)."""
    if first_alone:
        start_core(name + "-first", first_alone, hide_dns)
        ok_first, text_first = summary(collect(launch_apps(name + "-first", first_alone, 1)))
        print(f"  {name}: сначала только {','.join(first_alone)}\n{text_first}")
    else:
        ok_first = True
    start_core(name, plan, hide_dns, dns_via=dns_via)
    results = collect(launch_apps(name, plan + ["direct"], rounds))
    ok, text = summary(results)
    healthy, health = core_health()
    print(f"  {name}: {','.join(plan)} и direct одновременно, раундов {rounds}; {health}\n{text}")
    return ok and ok_first and healthy


def step_restart_under_load(name, plan):
    """Ядро перезапускается, пока все приложения передают данные; после перезапуска всё снова работает."""
    start_core(name, plan)
    running = launch_apps(name + "-load", plan + ["direct"], 6, pause=1)
    time.sleep(8)
    start_core(name + "-again", plan)
    loaded = collect(running)
    _, text = summary(loaded)
    print(f"  {name}: ядро перезапущено, пока все приложения передавали данные\n{text}")
    results = collect(launch_apps(name + "-after", plan + ["direct"], 1))
    ok, text = summary(results)
    healthy, health = core_health()
    print(f"  {name}: после перезапуска; {health}\n{text}")
    return ok and healthy


def step_bad_amnezia(name):
    """Подключение AmneziaWG, которое ядро не принимает: ядро должно сказать почему, не выдав закрытый ключ."""
    bad = dict(CONNECTIONS["A"], amnezia=dict(AMNEZIA["A"], h2="150000-160000"))
    config = build_config(["A", "D"])
    config["endpoints"][0] = dict(bad, tag="cA")
    path = os.path.join(WORK, f"core-{name}.json")
    with open(path, "w") as file:
        json.dump(config, file, indent=1)
    result = subprocess.run(netns("cli", SINGBOX, "run", "-c", path), capture_output=True, text=True, timeout=30)
    text = result.stdout + result.stderr
    leaked = CONNECTIONS["A"]["private_key"] in text or b64hex(CONNECTIONS["A"]["private_key"]) in text
    message = [line for line in text.splitlines() if "FATAL" in line or "h1 and h2" in line]
    ok = result.returncode != 0 and "h1 and h2 overlap" in text and not leaked
    print(f"  {name}: код выхода {result.returncode}, ключ в журнале={leaked}: {message[-1][-200:] if message else text[-300:]}")
    return ok


def step_first_packets(app, tries=4):
    """Новое ядро и сразу раунд приложения: его первые пакеты идут через новую сессию."""
    ok = True
    for attempt in range(tries):
        start_core(f"first-{app}-{attempt}", [app])
        results = collect(launch_apps(f"first-{app}-{attempt}", [app], 1, dl=1 << 20, ul=1 << 20, udp=5))
        good, text = summary(results)
        print(text)
        ok &= good
    return ok


def step_staggered(name, first, second, plan=None):
    """Одно ядро на оба приложения, как в лаунчере: второе запускается, пока первое передаёт данные, потом
    заканчивает, а первое продолжает."""
    start_core(name, plan or [first, second])
    running = launch_apps(name + "-1", [first], 20, pause=1)
    time.sleep(3)
    later = collect(launch_apps(name + "-2", [second, "direct"], 4))
    earlier = collect(running)
    after = collect(launch_apps(name + "-after", [first], 1))
    ok, text = summary({first: earlier[first], **later})
    ok_after, text_after = summary({first: after[first]})
    healthy, health = core_health()
    print(f"  {name}: {first} передаёт данные, {second} запускается позже и заканчивает; {health}\n{text}\n"
          f"  {name}: {first} после того, как {second} закончило\n{text_after}")
    return ok and ok_after and healthy


def warm_up():
    """Сервер amneziawg-go 3.1 сразу после запуска теряет первый пакет своего туннеля (ошибка сервера, не ядра).
    Один раунд, чтобы это прошло; его итог не считается. NO_WARMUP=1 — без него."""
    start_core("warm-up", ["A"])
    _, text = summary(collect(launch_apps("warm-up", ["A"], 1, dl=1 << 10, ul=1 << 10, udp=1)))
    print("разогрев (не считается):\n" + text, flush=True)
    stop_core()


def step_udp_flows(name, plan, rounds=12):
    """Подключения с одинаковым адресом в туннеле (10.66.0.2) и много новых потоков UDP сразу (правка sing-tun 0001)."""
    start_core(name, plan)
    results = collect(launch_apps(name, plan, rounds, dl=1 << 16, ul=1 << 16, udp=10))
    ok, text = summary(results)
    healthy, health = core_health()
    print(f"  {name}: {','.join(plan)} одновременно, раундов {rounds} с новыми потоками UDP; {health}\n{text}")
    return ok and healthy


def launch_hold(step, app, seconds):
    if app == "direct":
        host, expect, forbid = "10.99.0.1", "you are 10.99.0.2:", ""
    else:
        host = "site.test"
        expect, forbid = EXPECT[app]
    out = os.path.join(WORK, f"{step}-{app}-hold.json")
    command = netns("cli", interpreter(app), "-I", os.path.join(SCRIPTS, "app.py"), "--name", app, "--host", host,
                    "--expect", expect, "--forbid", forbid, "--hold", f"{seconds:.1f}", "--out", out)
    return subprocess.Popen(command, stdout=open(out + ".log", "w"), stderr=subprocess.STDOUT), out


def step_hold(name, order, stagger=2.0, tail=6.0):
    """Как в лаунчере: одно ядро ведёт все приложения из настроек, приложения запускаются по очереди в этом порядке,
    каждое всё время держит одно соединение TCP и один поток UDP, а тем временем запускаются и заканчивают короткие
    программы других приложений. Ни одно соединение не должно оборваться."""
    start_core(name, ["A", "B", "C", "D"])
    total = stagger * len(order) + tail
    holds = {}
    shorts = {}
    for index, app in enumerate(order):
        holds[app] = launch_hold(name, app, total - index * stagger)
        other = order[index - 1] if index else "direct"
        shorts.update({f"{key}#{index}": value for key, value in
                       launch_apps(f"{name}-short{index}", [other], 1, dl=1 << 18, ul=1 << 18, udp=3).items()})
        time.sleep(stagger)
    held = collect(holds, timeout=total + 30)
    short = collect(shorts, timeout=60)
    ok, text = summary(held)
    ok_short, text_short = summary(short)
    details = []
    for app, rounds in held.items():
        r = rounds[-1]
        details.append(f"{app}: TCP запросов {r.get('tcp_requests')}, UDP ответов {r.get('udp_echoes')}, "
                       f"потеряно {r.get('udp_lost')}, самая долгая пауза {r.get('udp_worst_gap_s')} с")
    healthy, health = core_health()
    print(f"  {name}: порядок запуска {','.join(order)}, каждое держит свои соединения; {health}\n{text}\n    " +
          "\n    ".join(details) + f"\n  короткие программы тем временем:\n{text_short}")
    return ok and ok_short and healthy


def log_size(name):
    return os.path.getsize(os.path.join(WORK, name + ".log"))


def log_count(name, since, text):
    with open(os.path.join(WORK, name + ".log"), errors="replace") as file:
        file.seek(since)
        return file.read().count(text)


def step_idle_keepalive(name):
    """Ядро держит подключения всех приложений из настроек. Подключение WireGuard с PersistentKeepalive (A: 20–30,
    B: 25), приложение которого не работает, не должно трогать свой сервер: другой клиент с тем же ключом потерял бы
    сессию (правка wireguard-go 0002). Когда его приложение работает, оно подключается как обычно."""
    marks = {app: log_size("server-" + app) for app in "AB"}
    start_core(name, ["A", "B", "C", "D"])
    vless = collect(launch_apps(name + "-vless", ["D"], 1))
    time.sleep(3)
    idle = {app: log_count("server-" + app, marks[app], "Received handshake initiation") for app in "AB"}
    used = collect(launch_apps(name + "-used", ["A", "B"], 1))
    ok_vless, text_vless = summary(vless)
    ok_used, text_used = summary(used)
    healthy, health = core_health()
    print(f"  {name}: работало только D; рукопожатий у серверов A и B: {idle}; {health}\n{text_vless}\n"
          f"  {name}: потом работали A и B\n{text_used}")
    return ok_vless and ok_used and healthy and not any(idle.values())


def step_core_stop_direct(name):
    """Наблюдение, не проверка: программа вне лаунчера (идёт напрямую через ядро) держит соединение TCP и поток UDP,
    открытые при работающем ядре, и ядро останавливается, как через 10 с после последнего приложения лаунчера."""
    start_core(name, ["A", "D"])
    # адрес вне локальной сети: в Windows такие соединения любой программы идут через ядро
    out = os.path.join(WORK, f"{name}-direct-hold.json")
    command = netns("cli", PYTHON, "-I", os.path.join(SCRIPTS, "app.py"), "--name", "direct", "--host", "203.0.113.10",
                    "--expect", "you are 10.99.0.2:", "--forbid", "", "--hold", "12", "--out", out)
    process = subprocess.Popen(command, stdout=open(out + ".log", "w"), stderr=subprocess.STDOUT)
    time.sleep(4)
    stop_core()
    held = collect({"direct": (process, out)}, timeout=60)
    r = held["direct"][-1]
    errors = str(r.get("errors"))[:300]
    print(f"  {name}: ядро остановлено, пока программа держала соединения: ok={r.get('ok')}, "
          f"TCP запросов {r.get('tcp_requests')}, UDP ответов {r.get('udp_echoes')}, потеряно {r.get('udp_lost')}, "
          f"ошибки: {errors}")
    return True


def step_connections_api(name, hide_dns):
    """Наблюдение, не проверка: что показывает clash API ядра, пока приложение лаунчера и программа вне лаунчера
    (идёт напрямую через ядро) держат соединения, программа вне лаунчера спрашивает DNS, и после того, как они
    закончили."""
    start_core(name, ["A", "D"], hide_dns=hide_dns, api=True)
    print(f"  {name}: ядро без соединений показывает {api_connections()}")
    out = os.path.join(WORK, f"{name}-direct-hold.json")
    direct = subprocess.Popen(netns("cli", PYTHON, "-I", os.path.join(SCRIPTS, "app.py"), "--name", "direct", "--host",
                                    "203.0.113.10", "--expect", "you are 10.99.0.2:", "--forbid", "", "--hold", "12",
                                    "--out", out), stdout=open(out + ".log", "w"), stderr=subprocess.STDOUT)
    held_a = launch_hold(name, "A", 12)
    lookup = subprocess.Popen(netns("cli", PYTHON, "-I", "-c", "import socket\ntry:\n print(socket.getaddrinfo('name.test', 80)[0][4])\n"
                                    "except Exception as e:\n print(repr(e))"), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    time.sleep(5)
    listed = api_connections() or []
    print(f"  {name}: пока они работают, соединений {len(listed)}:\n    " + "\n    ".join(describe_connection(c) for c in listed))
    collect({"direct": (direct, out), "A": held_a}, timeout=60)
    print(f"  {name}: DNS программы вне лаунчера: {lookup.communicate(timeout=60)[0].strip()}")
    ended = time.time()
    while time.time() - ended < 330:
        listed = api_connections() or []
        print(f"  {name}: через {time.time() - ended:.0f} с после конца: {len(listed)}: " +
              "; ".join(describe_connection(c)[:110] for c in listed), flush=True)
        if not listed:
            break
        time.sleep(10)
    stop_core()
    return True


DNS_QUERY = r"""
import random, socket, struct, sys, time
name, qtype, server, timeout = sys.argv[1], int(sys.argv[2]), sys.argv[3], float(sys.argv[4])
qid = random.randrange(65536)
query = struct.pack(">HHHHHH", qid, 0x0100, 1, 0, 0, 0) + b"".join(bytes([len(p)]) + p.encode() for p in name.split(".")) + \
    b"\0" + struct.pack(">HH", qtype, 1)
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
sock.settimeout(timeout)
started = time.time()
sock.sendto(query, (server, 53))
try:
    data = sock.recvfrom(4096)[0]
except socket.timeout:
    print("no answer in %.1f s" % (time.time() - started))
    sys.exit(0)
def skip(i):
    while data[i]:
        if data[i] & 0xC0 == 0xC0:
            return i + 2
        i += data[i] + 1
    return i + 1
count = struct.unpack(">H", data[6:8])[0]
i = skip(12) + 4
answers = []
for _ in range(count):
    i = skip(i)
    rtype, _, _, length = struct.unpack(">HHIH", data[i:i + 10])
    rdata = data[i + 10:i + 10 + length]
    i += 10 + length
    if rtype == 1:
        answers.append("A " + socket.inet_ntoa(rdata))
    elif rtype == 16:
        answers.append("TXT " + rdata[1:1 + rdata[0]].decode())
    else:
        answers.append("type %d" % rtype)
print("rcode %d, %s in %.1f s" % (data[3] & 15, answers, time.time() - started))
"""


def dns_query(qtype, name="site.test"):
    """Запрос программы вне лаунчера к DNS-серверу компьютера (так спрашивает служба DNS Windows)."""
    out = run(*netns("cli", PYTHON, "-I", "-c", DNS_QUERY, name, str(qtype), "198.51.100.53", "8"), check=False)
    return (out.stdout + out.stderr).strip()[-300:]


def step_rare_dns(name):
    """«Скрывать DNS-запросы» с основным подключением: редкие запросы программ вне лаунчера (TXT) идут к DNS основного
    подключения через него, и когда им не пользуется ни одно приложение, адреса по-прежнему подменяются, а без основного
    подключения редкие запросы идут к DNS компьютера, как раньше."""
    results = {}
    for case, plan, via in (("only-dns", ["A"], "D"), ("shared", ["D"], "D"), ("none", ["A"], None)):
        start_core(f"{name}-{case}", plan, hide_dns=True, dns_via=via)
        results[case] = (dns_query(16), dns_query(1))
        routed = [line.split("] ", 1)[-1][:160] for line in open(core_log) if "TXT" in line or "dns-c" in line]
        print(f"  {name} {case} (приложения {plan}, редкие DNS через {via}): TXT: {results[case][0]}; A: {results[case][1]}")
        for line in routed[-4:]:
            print("    ядро: " + line)
    stop_core()
    ok = all("via-vless" in results[case][0] and "198.18." in results[case][1] for case in ("only-dns", "shared")) and \
        "via-vless" not in results["none"][0] and "198.18." in results["none"][1]
    print(f"  {name}: {'ok' if ok else 'НЕ ПРОШЛО'}")
    return ok


def probe(port, user, password, *urls, mode="http"):
    """Запросы ConnectionTester через прокси на 127.0.0.1 (HttpClient, WebProxy с именем и ключом), см. probe/."""
    out = run(*netns("cli", "env", "DOTNET_ROOT=" + os.path.dirname(os.path.realpath(DOTNET)), DOTNET, PROBE, mode,
                     str(port), user or "-", password or "-", *urls), check=False)
    return (out.stdout + out.stderr).strip()


def hold_text(held):
    r = held["B"][-1]
    return (f"{'ok' if r.get('ok') else 'BAD'}: TCP запросов {r.get('tcp_requests')}, UDP ответов {r.get('udp_echoes')}, "
            f"потеряно {r.get('udp_lost')}, самая долгая пауза {r.get('udp_worst_gap_s')} с" +
            ("" if r.get("ok") else ", " + json.dumps(r)[:300]))


def auth_failures(since):
    lines = open(core_log).read().splitlines()[since:]
    return sum("authentication failed" in line for line in lines), len(open(core_log).read().splitlines())


TUNNEL_URL = "http://10.66.0.1/"
SITE_URL = "http://203.0.113.10/"
URL_OF = {"A": TUNNEL_URL, "B": TUNNEL_URL, "D": SITE_URL}


def step_check_via_core(name, mode):
    """Проверка подключения при работающем ядре: через прокси проверки в ядре (только с ключом, имя пользователя
    выбирает подключение), а приложение на том же WireGuard не теряет свои соединения."""
    if not (DOTNET and os.path.exists(PROBE)):
        return None
    start_core(name, ["A", "B", "D"], api=True, check=True)
    results = {}
    for case, user, password in (("без ключа", None, None), ("чужой ключ", "cB", "nope"), ("неизвестное подключение", "cZ", "bench")):
        results[case] = probe(CHECK, user, password, SITE_URL, mode=mode)
    # CONNECT (https) через прокси с ключом: туннель открывается, а TLS потом не проходит на обычном HTTP-сервере, не 407
    results["https с ключом"] = probe(CHECK, "cD", "bench", "https://203.0.113.10:80/", mode=mode)
    _, mark = auth_failures(0)
    for app in "ABD":
        results["через c" + app] = probe(CHECK, "c" + app, "bench", URL_OF[app], URL_OF[app], mode=mode)
    failures, _ = auth_failures(mark)
    for case, text in results.items():
        print(f"  {name}: {case}: " + text.replace("\n", "\n      "))
    print(f"  {name}: отказов при проверке с ключом (лишние запросы): {failures}")
    refused = (": 407 " if mode != "socks" else "error")
    ok = all(refused in results[c] for c in ("без ключа", "чужой ключ", "неизвестное подключение")) and \
        ("Ssl" in results["https с ключом"] or "SSL" in results["https с ключом"]) and \
        results["через cA"].count("hello from awg3.1") == 2 and results["через cB"].count("hello from plain-wg,") == 2 and \
        results["через cD"].count("hello from direct-server") == 2

    hold = launch_hold(name + "-new", "B", 16)
    time.sleep(5)
    new = probe(CHECK, "cB", "bench", TUNNEL_URL, TUNNEL_URL, TUNNEL_URL, mode=mode)
    held_new = collect({"B": hold}, timeout=60)
    print(f"  {name}: проверка через ядро, пока B держит соединения: " + new.replace("\n", "; ") + f"\n    B: {hold_text(held_new)}")
    ok &= new.count(": 200 in") == 3 and all(r.get("ok") for r in held_new["B"])
    healthy, health = core_health()
    print(f"  {name}: {health}; {'ok' if ok else 'НЕ ПРОШЛО'}")
    stop_core()
    return ok and healthy


def step_check_separate(name):
    """Наблюдение, не проверка: прежний способ для сравнения, отдельное ядро с тем же ключом WireGuard, пока приложение
    держит свои соединения (они обрываются: два туннеля с одним ключом отбивают друг у друга сервер)."""
    if not (DOTNET and os.path.exists(PROBE)):
        return None
    start_core(name, ["B"])
    hold = launch_hold(name, "B", 30)
    time.sleep(5)
    path = os.path.join(WORK, f"{name}-separate.json")
    with open(path, "w") as file:
        json.dump({"log": {"level": "info"},
                   "inbounds": [{"type": "mixed", "tag": "check-in", "listen": "127.0.0.1", "listen_port": 9097}],
                   "outbounds": [{"type": "direct", "tag": "direct"}],
                   "endpoints": [dict(CONNECTIONS["B"], tag="check")], "route": {"final": "check"}}, file)
    separate = subprocess.Popen(netns("cli", SINGBOX, "run", "-c", path), stdout=open(path + ".log", "w"), stderr=subprocess.STDOUT)
    time.sleep(1.5)
    old = probe(9097, None, None, TUNNEL_URL, TUNNEL_URL, TUNNEL_URL)
    separate.kill()
    separate.wait()
    held = collect({"B": hold}, timeout=60)
    print(f"  {name}: отдельное ядро с тем же ключом, пока B держит соединения: " + old.replace("\n", "; ") + f"\n    B: {hold_text(held)}")
    stop_core()
    return True


def launch_program(step, label, program, host, expect, forbid, rounds=1, udp=3):
    out = os.path.join(WORK, f"{step}-{label}.json")
    command = netns("cli", program, "-I", os.path.join(SCRIPTS, "app.py"), "--name", label, "--host", host,
                    "--expect", expect, "--forbid", forbid, "--rounds", str(rounds), "--out", out,
                    "--dl", str(1 << 16), "--ul", str(1 << 16), "--udp", str(udp))
    return subprocess.Popen(command, stdout=open(out + ".log", "w"), stderr=subprocess.STDOUT), out


def step_outside_copies(name):
    """Копия приложения D, запущенная мимо лаунчера (программа D: его папка, имя его программы), без метки лаунчера.
    «Через VPN» — через подключение D, «Блокировать» — без сети, «Разрешить» (без программ) — напрямую, как раньше.
    Само D каждый раз идёт через своё подключение. Шаблон написан большими буквами: ядро должно не различать регистр,
    как в Windows."""
    folder = os.path.join(ROOT, "outside", "Copy of D (x86)")
    copy = os.path.join(folder, "v2.1", "app")
    os.makedirs(os.path.dirname(copy), exist_ok=True)
    if not os.path.exists(copy):
        shutil.copy(PYTHON, copy)
    patterns = {"D": [outside_pattern(folder.upper(), "APP", True)]}
    expect, forbid = EXPECT["D"]
    ok = True
    for mode in ("vpn", "block", "allow"):
        start_core(f"{name}-{mode}", ["D"], outside=patterns if mode != "allow" else None, block_outside=mode == "block")
        running = launch_apps(f"{name}-{mode}", ["D"], 1, dl=1 << 16, ul=1 << 16, udp=3)
        if mode == "vpn":
            running["copy"] = launch_program(f"{name}-{mode}", "copy", copy, "site.test", expect, forbid)
        else:
            # адрес вне локальной сети: такие соединения любой программы идут через ядро. Напрямую без UDP: сервер
            # эха отвечает с 10.99.0.1, и поток UDP к 203.0.113.10 такой ответ не принимает.
            running["copy"] = launch_program(f"{name}-{mode}", "copy", copy, "203.0.113.10", "you are 10.99.0.2:", "",
                                             udp=3 if mode == "block" else 0)
        results = collect(running, timeout=120)
        copy_ok = all(r.get("ok") for r in results["copy"])
        log = open(core_log).read()
        rejected = len(re.findall(r"=> reject", log))
        want = copy_ok if mode != "block" else not copy_ok and rejected > 0
        d_ok = all(r.get("ok") for r in results["D"])
        healthy, health = core_health()
        detail = json.dumps(results["copy"][-1], ensure_ascii=False)[:300]
        print(f"  {name} {mode}: D {'ok' if d_ok else 'BAD'}, копия {'как ожидалось' if want else 'НЕ как ожидалось'} "
              f"(ok={copy_ok}, ядро отклонило {rejected}); {health}\n    копия: {detail}")
        ok &= want and d_ok and healthy
    return ok


def step_port_taken(name):
    """Другая программа заняла порт управления ядром (clash API) или порт его прокси проверки: ядро не запускается,
    и его последние строки говорят об этом (ControlServer.PortTaken ищет в них «bind» и порт)."""
    stop_core()
    ok = True
    for what, port in (("api", int(API.rsplit(":", 1)[1])), ("check", CHECK)):
        # SO_REUSEADDR: после шагов check-via-core-* на порту остаются закрытые соединения (TIME_WAIT), без него порт
        # не занять; занятый слушающий порт ядро (Go тоже ставит SO_REUSEADDR) всё равно не получит
        holder = subprocess.Popen(netns("cli", PYTHON, "-I", "-c",
                                        "import socket,time; s=socket.socket(); "
                                        "s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1); "
                                        "s.bind(('127.0.0.1', %d)); s.listen(); time.sleep(30)" % port))
        time.sleep(1)
        if holder.poll() is not None:
            print(f"  {name} {what}: не удалось занять порт {port} для проверки")
            ok = False
            continue
        path = os.path.join(WORK, f"core-{name}-{what}.json")
        with open(path, "w") as file:
            json.dump(build_config(["D"], api=True, check=True), file, indent=1)
        command = netns("cli", SINGBOX, "run", "-c", path)
        try:
            result = subprocess.run(command, capture_output=True, text=True, timeout=30)
        except subprocess.TimeoutExpired as error:
            # ядро запустилось, хотя порт занят
            result = subprocess.CompletedProcess(command, None, error.stdout or "", error.stderr or "")
        finally:
            holder.kill()
            holder.wait()
        lines = (result.stdout + result.stderr).strip().splitlines()
        found = [line for line in lines if "bind" in line.lower() and
                 any(int(m) == port for m in re.findall(r"127\.0\.0\.1:(\d{1,5})(?!\d)", line))]
        print(f"  {name} {what} (порт {port}): код выхода {result.returncode}, PortTaken увидит: {bool(found)}\n    "
              + (found[0] if found else "\n    ".join(lines[-3:]))[:400])
        ok &= result.returncode not in (0, None) and bool(found)
    return ok


def step_service_config(name):
    """Настройки ядра, которые строит сама служба (CoreConfigBuilder, программа cfgcheck/): все типы подключений,
    программы приложений, скрытие DNS, прокси проверки. Ядро должно принять их («sing-box check»)."""
    if not (DOTNET and os.path.exists(CFGCHECK)):
        return None
    folder = os.path.join(WORK, name)
    out = run(DOTNET, CFGCHECK, folder, check=False)
    if out.returncode != 0:
        print(f"  {name}: cfgcheck не отработал:\n" + (out.stdout + out.stderr)[-1500:])
        return False
    ok = True
    for file in sorted(os.listdir(folder)):
        result = run(SINGBOX, "check", "-c", os.path.join(folder, file), check=False)
        good = result.returncode == 0
        print(f"  {name}: {file}: {'ok' if good else 'ядро не принимает: ' + (result.stdout + result.stderr).strip()[-400:]}")
        ok &= good
    return ok


def all_steps():
    steps = {
        "service-config": lambda: step_service_config("service-config"),
        "mixed": lambda: step_concurrent("mixed", ["D", "A", "B", "C"], first_alone=["D"]),
        "mixed-reversed": lambda: step_concurrent("mixed-reversed", ["A", "C", "B", "D"], first_alone=["A"]),
        "awg-and-vless": lambda: step_concurrent("awg-and-vless", ["A", "D"], rounds=8),
        "awg-and-wg": lambda: step_concurrent("awg-and-wg", ["B", "A"], rounds=4),
        "hide-dns": lambda: step_concurrent("hide-dns", ["A", "B", "C", "D"], rounds=1, hide_dns=True),
        "restart": lambda: step_restart_under_load("restart", ["A", "B", "C", "D"]),
        "bad-amnezia": lambda: step_bad_amnezia("bad-amnezia"),
    }
    for app in "ABCD":
        steps["first-" + app] = (lambda app: lambda: step_first_packets(app))(app)
    # подключения с одинаковым адресом в туннеле (10.66.0.2), много потоков UDP сразу
    steps["same-address-wg-wg"] = lambda: step_udp_flows("same-address-wg-wg", ["B", "E"])
    steps["same-address-awg-wg"] = lambda: step_udp_flows("same-address-awg-wg", ["A", "B"])
    steps["same-address-awg-awg"] = lambda: step_udp_flows("same-address-awg-awg", ["A", "C"])
    # одно приложение запускается, пока работает другое; лаунчер ради этого ядро не перезапускает
    for first, second in (("B", "D"), ("D", "B"), ("A", "D"), ("D", "A"), ("B", "A"), ("A", "B"), ("C", "A")):
        name = f"{first}-then-{second}".replace("A", "awg").replace("B", "wg").replace("C", "awg1").replace("D", "vless")
        steps[name] = (lambda name, first, second: lambda: step_staggered(name, first, second))(name, first, second)
    # новое подключение в настройках: ядро перезапускается с ним, после того как первое приложение работало одно
    steps["wg-then-vless-new-core"] = lambda: step_concurrent("wg-then-vless-new-core", ["B", "D"], first_alone=["B"])
    steps["awg-then-vless-new-core"] = lambda: step_concurrent("awg-then-vless-new-core", ["A", "D"], first_alone=["A"])
    steps["idle-keepalive"] = lambda: step_idle_keepalive("idle-keepalive")
    steps["rare-dns"] = lambda: step_rare_dns("rare-dns")
    # редкие DNS-запросы через основное подключение: им не пользуется ни одно приложение или пользуется одно из них
    steps["hide-dns-via-main"] = lambda: step_concurrent("hide-dns-via-main", ["A", "B", "C"], rounds=1, hide_dns=True, dns_via="D")
    steps["hide-dns-via-shared"] = lambda: step_concurrent("hide-dns-via-shared", ["A", "B", "C", "D"], rounds=1, hide_dns=True,
                                                           dns_via="D")
    for mode in ("http", "http-pre", "socks"):
        steps["check-via-core-" + mode] = (lambda mode: lambda: step_check_via_core("check-via-core-" + mode, mode))(mode)
    steps["outside-copies"] = lambda: step_outside_copies("outside-copies")
    steps["port-taken"] = lambda: step_port_taken("port-taken")
    # долгие соединения, пока приложения запускаются в любом порядке (ядро ради этого не перезапускается)
    for order in itertools.permutations("ABCD"):
        steps["hold-" + "".join(order)] = (lambda order: lambda: step_hold("hold-" + "".join(order), list(order)))(order)
    return steps


# Наблюдения: печатают, что происходит, и всегда «проходят»; в полный прогон не входят, запускаются по имени
OBSERVATIONS = {
    "core-stop-direct": lambda: step_core_stop_direct("core-stop-direct"),
    "connections-api": lambda: step_connections_api("connections-api", False),
    "connections-api-hide-dns": lambda: step_connections_api("connections-api-hide-dns", True),
    "check-separate": lambda: step_check_separate("check-separate"),
}


def main():
    steps = all_steps()
    if LIST_ONLY:
        print("Шаги полного прогона:\n  " + "\n  ".join(steps) + "\nНаблюдения (только по имени):\n  " + "\n  ".join(OBSERVATIONS))
        return
    steps.update({k: v for k, v in OBSERVATIONS.items() if k in STEPS})
    unknown = [step for step in STEPS if step not in steps]
    if unknown:
        raise SystemExit("Нет таких шагов: " + ", ".join(unknown) + " (список: bench.sh --list)")
    os.makedirs(WORK)
    setup_network()
    setup_servers()
    time.sleep(1)
    if not os.environ.get("NO_WARMUP"):
        warm_up()
    failed, skipped = [], []
    for step in STEPS or [s for s in steps if s not in OBSERVATIONS]:
        started = time.time()
        print(f"{step}:", flush=True)
        result = steps[step]()
        if result is None:
            skipped.append(step)
            print("  пропущен: нет .NET 8 SDK (dotnet) или программы probe/cfgcheck")
        elif not result:
            failed.append(step)
        print(f"  ({time.time() - started:.0f} с)", flush=True)
    print(("НЕ ПРОШЛИ: " + ", ".join(failed) if failed else "ВСЁ ПРОШЛО") +
          (" | пропущены: " + ", ".join(skipped) if skipped else "") + " | журналы: " + WORK)
    if failed:
        sys.exit(1)


try:
    main()
finally:
    if not LIST_ONLY:
        stop_core()
        for process in processes:
            process.kill()
        for name in ("srv", "cli"):
            run("ip", "netns", "del", name, check=False)
