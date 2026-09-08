package httpapi

import (
	"context"
	"errors"
	"net"
	"net/http"
	"net/netip"
	"strings"
	"sync"
	"time"

	"fanfh.cn/superpan/ota-admin/internal/licensing/model"
	"fanfh.cn/superpan/ota-admin/internal/licensing/service"
)

type Clock interface {
	Now() time.Time
}

type LicenseService interface {
	Activate(context.Context, string, string, string, string) (model.ActivationState, error)
	Validate(context.Context, string, string, string) (model.ActivationState, error)
}

type AdminService interface {
	ListSoftware(context.Context, int, int) ([]model.Software, int, error)
	CreateSoftware(context.Context, string) (model.Software, error)
	RenameSoftware(context.Context, string, string) error
	SetSoftwareEnabled(context.Context, string, bool) error
	ListPackages(context.Context, int, int) ([]model.Package, int, error)
	CreatePackage(context.Context, string, []string) (model.Package, error)
	RenamePackage(context.Context, string, string) error
	ReplacePackageMembers(context.Context, string, []string) error
	PackageRemovalImpact(context.Context, string, []string) (int, error)
	GenerateCards(context.Context, string, int, int, *string, []*string) ([]service.GeneratedCard, error)
	SearchAdminCards(context.Context, string, model.AdminCardFilter, int, int) ([]model.AdminCardSummary, int, error)
	CopyCard(context.Context, string) (model.AdminExportCard, error)
	ExportCards(context.Context, []string, int) ([]model.AdminExportCard, error)
	RemarkCard(context.Context, string, *string) error
	RenewCard(context.Context, string, int) error
	UnbindCard(context.Context, string) error
	DeleteCard(context.Context, string) error
	ListDevices(context.Context, int, int) ([]model.DeviceSummary, int, error)
	GetDevice(context.Context, string) (model.DeviceSummary, error)
	UnbindDevice(context.Context, string) error
	ProtectAdminReplay(string, []byte) ([]byte, error)
	OpenAdminReplay(string, []byte) ([]byte, error)
}

type AdminIdempotencyStore interface {
	GetAdminIdempotency(context.Context, string, time.Time) (model.AdminIdempotencyRecord, bool, error)
	PutAdminIdempotency(context.Context, string, model.AdminIdempotencyRecord, time.Time, int) error
	CompleteAdminIdempotency(context.Context, string, string, model.AdminIdempotencyRecord) error
}

type ReplayStore interface {
	ClaimReplayNonce(context.Context, string, time.Time, time.Time, int) (bool, error)
}

type ClaimsSigner interface {
	Sign(model.Claims) (model.SignedEnvelope, error)
}

type Options struct {
	Service               LicenseService
	ReplayStore           ReplayStore
	Signer                ClaimsSigner
	Clock                 Clock
	MaxBodyBytes          int64
	MaxHeaderBytes        int
	TimestampWindow       time.Duration
	NonceLifetime         time.Duration
	MaxReplayNonces       int
	ActivationRate        RateLimit
	ValidationRate        RateLimit
	MaxLimiterEntries     int
	AdminService          AdminService
	AdminIdempotencyStore AdminIdempotencyStore
	InternalSecret        []byte
	AdminRate             RateLimit
	MaxAdminPageSize      int
	MaxExportCount        int
}

type api struct {
	options       Options
	activation    *fixedWindowLimiter
	validation    *fixedWindowLimiter
	admin         *fixedWindowLimiter
	adminMutation sync.Mutex
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
	adminConfigured := options.AdminService != nil || options.AdminIdempotencyStore != nil || len(options.InternalSecret) != 0 || options.AdminRate.Requests != 0 || options.MaxAdminPageSize != 0 || options.MaxExportCount != 0
	if adminConfigured {
		if options.AdminService == nil || options.AdminIdempotencyStore == nil || len(options.InternalSecret) < 16 || options.AdminRate.Requests < 1 || options.AdminRate.Window <= 0 || options.MaxAdminPageSize < 1 || options.MaxExportCount < 1 {
			return nil, errors.New("internal admin API dependencies are invalid")
		}
		a.options.InternalSecret = append([]byte(nil), options.InternalSecret...)
		a.admin = newFixedWindowLimiter(options.AdminRate, options.MaxLimiterEntries, options.Clock)
		adminMux := http.NewServeMux()
		adminMux.HandleFunc("GET /internal/v1/software", a.listSoftware)
		adminMux.HandleFunc("POST /internal/v1/software", a.createSoftware)
		adminMux.HandleFunc("PATCH /internal/v1/software/{id}", a.renameSoftware)
		adminMux.HandleFunc("POST /internal/v1/software/{id}/enable", a.enableSoftware)
		adminMux.HandleFunc("POST /internal/v1/software/{id}/disable", a.disableSoftware)
		adminMux.HandleFunc("GET /internal/v1/packages", a.listPackages)
		adminMux.HandleFunc("POST /internal/v1/packages", a.createPackage)
		adminMux.HandleFunc("PATCH /internal/v1/packages/{id}", a.renamePackage)
		adminMux.HandleFunc("PUT /internal/v1/packages/{id}/members", a.replacePackageMembers)
		adminMux.HandleFunc("POST /internal/v1/packages/{id}/impact", a.packageImpact)
		adminMux.HandleFunc("GET /internal/v1/cards", a.searchCards)
		adminMux.HandleFunc("POST /internal/v1/cards/generate", a.generateCards)
		adminMux.HandleFunc("POST /internal/v1/cards/export", a.exportCards)
		adminMux.HandleFunc("GET /internal/v1/cards/{id}/copy", a.copyCard)
		adminMux.HandleFunc("PATCH /internal/v1/cards/{id}/remark", a.remarkCard)
		adminMux.HandleFunc("POST /internal/v1/cards/{id}/renew", a.renewCard)
		adminMux.HandleFunc("POST /internal/v1/cards/{id}/unbind", a.unbindCard)
		adminMux.HandleFunc("DELETE /internal/v1/cards/{id}", a.deleteCard)
		adminMux.HandleFunc("GET /internal/v1/devices", a.listDevices)
		adminMux.HandleFunc("GET /internal/v1/devices/{id}", a.getDevice)
		adminMux.HandleFunc("POST /internal/v1/devices/{id}/unbind", a.unbindDevice)
		mux.Handle("/internal/", a.authorizeInternal(adminMux))
	}
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
