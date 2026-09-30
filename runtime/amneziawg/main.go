package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/netip"
	"os"
	"os/signal"
	"strconv"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
)

type configuration struct {
	UAPI            string   `json:"uapi"`
	Addresses       []string `json:"addresses"`
	DNS             []string `json:"dns"`
	MTU             int      `json:"mtu"`
	Username        string   `json:"username"`
	Password        string   `json:"password"`
	SourceAddress   string   `json:"sourceAddress"`
	SourceInterface uint32   `json:"sourceInterface"`
	ReadyFile       string   `json:"readyFile"`
}

func readConfiguration(path string) (configuration, error) {
	var cfg configuration
	f, err := os.Open(path)
	if err != nil {
		return cfg, err
	}
	defer f.Close()
	dec := json.NewDecoder(io.LimitReader(f, 2*1024*1024+1))
	dec.DisallowUnknownFields()
	if err := dec.Decode(&cfg); err != nil {
		return cfg, errors.New("invalid configuration")
	}
	var trailing any
	if dec.Decode(&trailing) != io.EOF {
		return cfg, errors.New("invalid trailing configuration")
	}
	if cfg.MTU < 576 || cfg.MTU > 65535 || len(cfg.Username) < 16 || len(cfg.Username) > 255 || len(cfg.Password) < 32 || len(cfg.Password) > 255 || len(cfg.Addresses) == 0 || len(cfg.DNS) == 0 {
		return cfg, errors.New("invalid configuration fields")
	}
	return cfg, nil
}

func addresses(values []string) ([]netip.Addr, error) {
	result := make([]netip.Addr, 0, len(values))
	for _, value := range values {
		address, err := netip.ParseAddr(value)
		if err != nil || address.IsUnspecified() || address.IsMulticast() || address.Zone() != "" {
			return nil, errors.New("invalid address")
		}
		result = append(result, address)
	}
	return result, nil
}

func createDevice(cfg configuration, validate bool) (*device.Device, *netstack.Net, error) {
	local, err := addresses(cfg.Addresses)
	if err != nil {
		return nil, nil, err
	}
	dns, err := addresses(cfg.DNS)
	if err != nil {
		return nil, nil, err
	}
	tun, network, err := netstack.CreateNetTUN(local, dns, cfg.MTU)
	if err != nil {
		return nil, nil, errors.New("virtual stack unavailable")
	}
	logger := &device.Logger{Verbosef: func(string, ...any) {}, Errorf: func(string, ...any) {}}
	dev := device.NewDevice(tun, &udpBind{source: cfg.SourceAddress, interfaceIndex: cfg.SourceInterface, dryRun: validate}, logger)
	if err := dev.IpcSet(cfg.UAPI); err != nil {
		dev.Close()
		// UAPI errors can echo key material or packet templates. Never return the raw error.
		return nil, nil, errors.New("AmneziaWG rejected the configuration")
	}
	return dev, network, nil
}

func run(ctx context.Context, cfg configuration) error {
	dev, network, err := createDevice(cfg, false)
	if err != nil {
		return err
	}
	defer dev.Close()
	if err := dev.Up(); err != nil {
		return errors.New("AmneziaWG socket unavailable")
	}
	server, err := newSOCKSServer(network, cfg.Username, cfg.Password)
	if err != nil {
		return errors.New("SOCKS listener unavailable")
	}
	defer server.close()
	if cfg.ReadyFile != "" {
		// Write atomically so the host never observes a partial port number.
		if err := os.WriteFile(cfg.ReadyFile+".tmp", []byte(strconv.Itoa(server.port())), 0600); err != nil {
			return errors.New("readiness unavailable")
		}
		if err := os.Rename(cfg.ReadyFile+".tmp", cfg.ReadyFile); err != nil {
			return errors.New("readiness unavailable")
		}
		defer os.Remove(cfg.ReadyFile)
	}
	fmt.Printf("READY_%d\n", server.port())
	done := make(chan error, 1)
	go func() { done <- server.serve(ctx) }()
	select {
	case <-ctx.Done():
		return nil
	case <-dev.Wait():
		return errors.New("AmneziaWG stopped")
	case <-done:
		if ctx.Err() != nil {
			return nil
		}
		return errors.New("SOCKS listener stopped")
	}
}

func main() {
	configPath := flag.String("config", "", "protected configuration file")
	owner := flag.Int("owner", 0, "owning process")
	validate := flag.Bool("validate", false, "validate configuration without opening transport sockets")
	version := flag.Bool("version", false, "runtime version")
	flag.Parse()
	if *version {
		fmt.Println("DiTunnel AmneziaWG bridge 1 b5928efb6ca1")
		return
	}
	cfg, err := readConfiguration(*configPath)
	if err == nil && *validate {
		var dev *device.Device
		dev, _, err = createDevice(cfg, true)
		if dev != nil {
			dev.Close()
		}
		if err == nil {
			fmt.Println("VALID")
			return
		}
	} else if err == nil {
		ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt)
		defer cancel()
		if *owner <= 0 {
			err = errors.New("owner required")
		} else {
			watch, watchErr := watchOwner(*owner)
			if watchErr != nil {
				err = errors.New("owner unavailable")
			} else {
				defer watch.close()
				go func() {
					tick := time.NewTicker(250 * time.Millisecond)
					defer tick.Stop()
					for {
						select {
						case <-ctx.Done():
							return
						case <-tick.C:
							if watch.exited() {
								cancel()
								return
							}
						}
					}
				}()
				err = run(ctx, cfg)
			}
		}
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "ERROR_AWG_RUNTIME")
		os.Exit(1)
	}
}
