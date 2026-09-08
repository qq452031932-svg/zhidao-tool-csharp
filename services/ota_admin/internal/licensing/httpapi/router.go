package httpapi

import (
	"context"
	"errors"
	"net"
	"net/http"
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
		deferred := true
		defer func() {
			if deferred && recover() != nil {
				a.writeClaims(w, http.StatusServiceUnavailable, fallbackRequestID, model.CodeServiceUnavailable, nil)
			}
		}()
		if headerBytes(r.Header) > a.options.MaxHeaderBytes {
			deferred = false
			a.writeClaims(w, http.StatusBadRequest, fallbackRequestID, model.CodeInvalidRequest, nil)
			return
		}
		next.ServeHTTP(w, r)
		deferred = false
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

func clientKey(remoteAddress string) string {
	host, _, err := net.SplitHostPort(remoteAddress)
	if err == nil && host != "" {
		return host
	}
	if len(remoteAddress) > 256 {
		return remoteAddress[:256]
	}
	return strings.TrimSpace(remoteAddress)
}
