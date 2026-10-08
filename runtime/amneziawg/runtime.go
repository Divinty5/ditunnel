package main

import (
	"encoding/json"
	"errors"
	"github.com/amnezia-vpn/amneziawg-go/v3/device"
	"github.com/amnezia-vpn/amneziawg-go/v3/tun/netstack"
	"io"
	"net/netip"
	"os"
	"strings"
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
	source, _ := netip.ParseAddr(cfg.SourceAddress)
	ipv6 := source.Is6()
	for _, line := range strings.Split(cfg.UAPI, "\n") {
		if value, found := strings.CutPrefix(line, "endpoint="); found {
			endpoint, _ := netip.ParseAddrPort(value)
			ipv6 = ipv6 || endpoint.Addr().Is6()
		}
	}
	return createDeviceWithBind(cfg, &udpBind{source: cfg.SourceAddress, interfaceIndex: cfg.SourceInterface, dryRun: validate, ipv6: ipv6})
}

func createDeviceWithBind(cfg configuration, bind *udpBind) (*device.Device, *netstack.Net, error) {
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
	dev := device.NewDevice(tun, bind, logger)
	if err := dev.IpcSet(cfg.UAPI); err != nil {
		dev.Close()
		// UAPI errors can echo key material or packet templates. Never return the raw error.
		return nil, nil, errors.New("AmneziaWG rejected the configuration")
	}
	return dev, network, nil
}
