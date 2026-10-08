//go:build !android

package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"strconv"
	"time"

	"github.com/amnezia-vpn/amneziawg-go/v3/device"
)

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
		ctx, cancel := signal.NotifyContext(context.Background(), terminationSignals...)
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
