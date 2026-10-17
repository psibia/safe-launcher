// awgserver: тестовый сервер AmneziaWG (и WireGuard) с сетью внутри процесса, без адаптера в системе.
// Внутри туннеля: HTTP на 10.66.0.1:80, эхо UDP на 10.66.0.1:7 и DNS на 10.66.0.1:53 и 1.1.1.1:53
// (UDP и TCP), который на любой запрос A отвечает 10.66.0.1.
package main

import (
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"flag"
	"fmt"
	"io"
	"log"
	"math/rand"
	"net"
	"net/http"
	"net/netip"
	"os"
	"strconv"

	"github.com/amnezia-vpn/amneziawg-go/conn"
	"github.com/amnezia-vpn/amneziawg-go/device"
	"github.com/amnezia-vpn/amneziawg-go/tun/netstack"
)

var tunnelAddress = net.IPv4(10, 66, 0, 1).To4()

// dnsAnswer отвечает на запрос DNS: для A адрес tunnelAddress, для остальных типов пустой ответ.
func dnsAnswer(query []byte) []byte {
	if len(query) < 12 || binary.BigEndian.Uint16(query[4:6]) != 1 {
		return nil
	}
	end := 12
	for end < len(query) && query[end] != 0 {
		end += int(query[end]) + 1
	}
	end += 5 // нулевая метка, тип, класс
	if end > len(query) {
		return nil
	}
	qtype := binary.BigEndian.Uint16(query[end-4 : end-2])
	response := append([]byte{}, query[:end]...)
	response[2] = 0x81 // ответ, рекурсия запрошена
	response[3] = 0x80 // рекурсия доступна, без ошибки
	binary.BigEndian.PutUint16(response[8:10], 0)
	binary.BigEndian.PutUint16(response[10:12], 0)
	if qtype == 1 {
		binary.BigEndian.PutUint16(response[6:8], 1)
		response = append(response, 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4)
		response = append(response, tunnelAddress...)
	} else {
		binary.BigEndian.PutUint16(response[6:8], 0)
	}
	return response
}

func main() {
	uapiPath := flag.String("uapi", "", "configuration protocol lines")
	label := flag.String("label", "", "name in the hello reply")
	flag.Parse()

	addresses := []netip.Addr{netip.MustParseAddr("10.66.0.1"), netip.MustParseAddr("1.1.1.1")}
	tunDevice, tnet, err := netstack.CreateNetTUN(addresses, nil, 1420)
	if err != nil {
		log.Fatal(err)
	}
	wgDevice := device.NewDevice(tunDevice, conn.NewDefaultBind(), device.NewLogger(device.LogLevelVerbose, "server: "))
	uapi, err := os.ReadFile(*uapiPath)
	if err != nil {
		log.Fatal(err)
	}
	if err := wgDevice.IpcSet(string(uapi)); err != nil {
		log.Fatal("ipc: ", err)
	}
	if err := wgDevice.Up(); err != nil {
		log.Fatal(err)
	}

	udpEcho, err := tnet.ListenUDP(&net.UDPAddr{IP: tunnelAddress, Port: 7})
	if err != nil {
		log.Fatal(err)
	}
	go func() {
		buffer := make([]byte, 65535)
		for {
			n, addr, err := udpEcho.ReadFrom(buffer)
			if err != nil {
				return
			}
			udpEcho.WriteTo(buffer[:n], addr)
		}
	}()

	for _, address := range addresses {
		dnsUDP, err := tnet.ListenUDP(&net.UDPAddr{IP: address.AsSlice(), Port: 53})
		if err != nil {
			log.Fatal(err)
		}
		go func() {
			buffer := make([]byte, 65535)
			for {
				n, addr, err := dnsUDP.ReadFrom(buffer)
				if err != nil {
					return
				}
				if response := dnsAnswer(buffer[:n]); response != nil {
					dnsUDP.WriteTo(response, addr)
				}
			}
		}()
		dnsTCP, err := tnet.ListenTCP(&net.TCPAddr{IP: address.AsSlice(), Port: 53})
		if err != nil {
			log.Fatal(err)
		}
		go func() {
			for {
				c, err := dnsTCP.Accept()
				if err != nil {
					return
				}
				go func() {
					defer c.Close()
					for {
						var length [2]byte
						if _, err := io.ReadFull(c, length[:]); err != nil {
							return
						}
						query := make([]byte, binary.BigEndian.Uint16(length[:]))
						if _, err := io.ReadFull(c, query); err != nil {
							return
						}
						response := dnsAnswer(query)
						if response == nil {
							return
						}
						binary.BigEndian.PutUint16(length[:], uint16(len(response)))
						c.Write(append(length[:], response...))
					}
				}()
			}
		}()
	}

	listener, err := tnet.ListenTCP(&net.TCPAddr{IP: tunnelAddress, Port: 80})
	if err != nil {
		log.Fatal(err)
	}
	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		fmt.Fprintf(w, "hello from %s, you are %s\n", *label, r.RemoteAddr)
	})
	mux.HandleFunc("/bytes", func(w http.ResponseWriter, r *http.Request) {
		n, _ := strconv.Atoi(r.URL.Query().Get("n"))
		data := make([]byte, n)
		rand.Read(data)
		sum := sha256.Sum256(data)
		w.Header().Set("X-Sum", hex.EncodeToString(sum[:]))
		w.Header().Set("Content-Length", strconv.Itoa(n))
		w.Write(data)
	})
	mux.HandleFunc("/upload", func(w http.ResponseWriter, r *http.Request) {
		hash := sha256.New()
		n, _ := io.Copy(hash, r.Body)
		fmt.Fprintf(w, "%d %x\n", n, hash.Sum(nil))
	})
	log.Fatal(http.Serve(listener, mux))
}
