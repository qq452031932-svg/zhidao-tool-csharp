package httpapi

import (
	"context"
	"errors"
	"net"
	"net/http"
	"net/netip"
	"strings"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
)

type Clock interface {
	Now() time.Time
}

type LicenseService interface {
	Activate(context.Context, string, string, string, string) (model.ActivationState, error)
	Validate(context.Context, string, string, string) (model.ActivationState, error)
}

type ReplayStore interface {
	ClaimReplayNonce(context.Context, string, time.Time, time.Time, int) (bool, error)
}

type ClaimsSigner interface {
	Sign(model.Claims) (model.SignedEnvelope, error)
}

type Options struct {
	Service           LicenseService
	ReplayStore       ReplayStore
	Signer            ClaimsSigner
	Clock             Clock
	MaxBodyBytes      int64
	MaxHeaderBytes    int
	TimestampWindow   time.Duration
	NonceLifetime     time.Duration
	MaxReplayNonces   int
	ActivationRate    RateLimit
	ValidationRate    RateLimit
	MaxLimiterEntries int
}

type api struct {
	options    Options
	activation *fixedWindowLimiter
	validation *fixedWindowLimiter
}

func NewRouter(options Options) (http.Handler, error) {
	if options.Service == nil || options.ReplayStore == nil || options.Signer == nil || options.Clock == nil {
		return nil, errors.New("license API dependencies are required")
	}
	if options.MaxBodyBytes < 1 || options.MaxHeaderBytes < 1 || options.TimestampWindow <= 0 || options.NonceLifetime < options.TimestampWindow || options.MaxReplayNonces < 1 || options.ActivationRate.Requests < 1 || options.ActivationRate.Window <= 0 || options.ValidationRate.Requests < 1 || options.ValidationRate.Window <= 0 || options.MaxLimiterEntries < 1 {
		return nil, errors.New("license API limits are invalid")
	}
	a := &api{options: options}
	a.activation = newFixedWindowLimiter(options.ActivationRate, options.MaxLimiterEntries, options.Clock)
	a.validation = newFixedWindowLimiter(options.ValidationRate, options.MaxLimiterEntries, options.Clock)
	mux := http.NewServeMux()
	mux.HandleFunc("POST /license-api/v1/activate", a.activate)
	mux.HandleFunc("POST /license-api/v1/validate", a.validate)
	return a.protect(mux), nil
}

func (a *api) protect(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		defer func() {
			if recover() != nil {
				a.writeUnsignedCode(w, http.StatusServiceUnavailable, model.CodeServiceUnavailable)
			}
		}()
		next.ServeHTTP(w, r)
	})
}

func headerBytes(headers http.Header) int {
	total := 0
	for name, values := range headers {
		total += len(name) + 2
		for _, value := range values {
			total += len(value) + 2
		}
	}
	return total
}

func clientKey(request *http.Request) string {
	peer := canonicalRemoteIP(request.RemoteAddr)
	if peer.IsValid() && peer.IsLoopback() {
		values := request.Header.Values("X-Real-IP")
		if len(values) == 1 {
			value := values[0]
			if len(value) <= 45 && value == strings.TrimSpace(value) && !strings.Contains(value, ",") {
				if forwarded, err := netip.ParseAddr(value); err == nil && forwarded.Zone() == "" {
					return forwarded.Unmap().String()
				}
			}
		}
	}
	if peer.IsValid() {
		return peer.Unmap().String()
	}
	return "unknown-peer"
}

func canonicalRemoteIP(remoteAddress string) netip.Addr {
	host, _, err := net.SplitHostPort(remoteAddress)
	if err != nil {
		return netip.Addr{}
	}
	address, err := netip.ParseAddr(host)
	if err != nil || address.Zone() != "" {
		return netip.Addr{}
	}
	return address.Unmap()
}
