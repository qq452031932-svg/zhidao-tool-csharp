package httpapi

import (
	"context"
	"errors"
	"io"
	"net/http"
	"regexp"
	"strings"

	"fanfh.cn/superpan/ota-admin/internal/candidate"
	"fanfh.cn/superpan/ota-admin/internal/deploy"
	"fanfh.cn/superpan/ota-admin/internal/licenseadmin"
	"fanfh.cn/superpan/ota-admin/internal/security"
	"fanfh.cn/superpan/ota-admin/internal/session"
	"fanfh.cn/superpan/ota-admin/internal/store"
	"fanfh.cn/superpan/ota-admin/internal/upload"
	"fanfh.cn/superpan/ota-admin/internal/webui"
)

const (
	preauthCookieName = "ota_admin_preauth"
	sessionCookieName = "ota_admin_session"
)

type Options struct {
	Sessions          *session.Service
	PublicOrigin      string
	MaxJSONBytes      int64
	Uploads           UploadReceiver
	UploadEnvelopes   EnvelopeAcceptor
	Publisher         CandidatePublisher
	ReleaseOperations ReleaseOperations
	LicenseAdmin      *licenseadmin.Client
}

type CandidatePublisher interface {
	PublishForActiveSession(context.Context, string, string, string) (deploy.PublishResult, error)
}

type ReleaseOperations interface {
	ReleaseHistory(context.Context) ([]store.ReleaseHistoryItem, error)
	RollbackForActiveSession(context.Context, session.Principal, string, string, string) (deploy.RollbackResult, error)
}

type UploadReceiver interface {
	Receive(context.Context, io.Reader, int64) (*upload.Envelope, error)
}

// EnvelopeAcceptor is the Task 9 integration seam. Reserve must atomically bind
// the active session and idempotency key before request bytes are consumed.
// Accept must reverify the same session at its commit boundary; a successful
// result transfers ownership and the cleanup obligation. Pending and terminal
// results are replayed for the same active session and operation ID.
type EnvelopeAcceptor interface {
	ReserveEnvelopeForActiveSession(context.Context, string, string) (candidate.Summary, bool, error)
	RenewEnvelopeLeaseForActiveSession(context.Context, string, string, string) error
	FailEnvelopeForActiveSession(context.Context, string, string, string, string) (candidate.Summary, error)
	CandidateForOperationForActiveSession(context.Context, string, string) (candidate.Summary, bool, error)
	AcceptEnvelopeForActiveSession(context.Context, string, string, *upload.Envelope, string) (candidate.Summary, error)
	CurrentForActiveSession(context.Context, string) (candidate.Summary, error)
	StatusForActiveSession(context.Context, string, string) (candidate.Summary, error)
}

var canonicalOperationID = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`)

type Router struct {
	sessions           *session.Service
	publicOrigin       string
	maxJSONBytes       int64
	uploads            UploadReceiver
	uploadEnvelopes    EnvelopeAcceptor
	publisher          CandidatePublisher
	releaseOperations  ReleaseOperations
	licenseAdminClient *licenseadmin.Client
	web                http.Handler
}

func NewRouter(options Options) (http.Handler, error) {
	if options.Sessions == nil {
		return nil, errors.New("session service is required")
	}
	if strings.TrimSpace(options.PublicOrigin) == "" {
		return nil, errors.New("public origin is required")
	}
	if options.MaxJSONBytes <= 0 {
		return nil, errors.New("maximum JSON body size must be positive")
	}
	publisher := options.Publisher
	if publisher == nil && options.ReleaseOperations != nil {
		publisher, _ = options.ReleaseOperations.(CandidatePublisher)
	}
	router := &Router{
		sessions: options.Sessions, publicOrigin: options.PublicOrigin, maxJSONBytes: options.MaxJSONBytes,
		uploads: options.Uploads, uploadEnvelopes: options.UploadEnvelopes, publisher: publisher, releaseOperations: options.ReleaseOperations,
		licenseAdminClient: options.LicenseAdmin,
		web:                webui.Handler(),
	}
	return withAdminSecurityHeaders(router), nil
}

func (router *Router) ServeHTTP(response http.ResponseWriter, request *http.Request) {
	licensePath := request.URL.Path == licenseAdminPrefix || strings.HasPrefix(request.URL.Path, licenseAdminPrefix+"/")
	allowedMethod := request.Method == http.MethodGet || request.Method == http.MethodPost || licensePath && (request.Method == http.MethodPatch || request.Method == http.MethodPut || request.Method == http.MethodDelete)
	if !allowedMethod {
		response.Header().Set("Allow", "GET, POST")
		writeError(response, http.StatusMethodNotAllowed, "method_not_allowed")
		return
	}
	if request.Method == http.MethodGet && isWebAssetPath(request.URL.Path) {
		router.web.ServeHTTP(response, request)
		return
	}
	switch {
	case request.URL.Path == "/admin/api/session/bootstrap" && request.Method == http.MethodGet:
		router.bootstrap(response, request)
	case request.URL.Path == "/admin/api/session/login" && request.Method == http.MethodPost:
		router.login(response, request)
	case strings.HasPrefix(request.URL.Path, "/admin/api/"):
		router.authenticated(response, request)
	default:
		writeError(response, http.StatusNotFound, "not_found")
	}
}

func isWebAssetPath(path string) bool {
	switch path {
	case "/admin/", "/admin/index.html", "/admin/styles.css", "/admin/app.js":
		return true
	default:
		return false
	}
}

func (router *Router) authenticated(response http.ResponseWriter, request *http.Request) {
	mutating := request.Method != http.MethodGet
	if mutating && !security.SameOriginRequest(request, router.publicOrigin) {
		writeError(response, http.StatusForbidden, "request_rejected")
		return
	}
	cookie, ok := uniqueCookie(request, sessionCookieName)
	if !ok {
		writeError(response, http.StatusUnauthorized, "authentication_failed")
		return
	}
	csrfToken, csrfOK := uniqueHeader(request, "X-CSRF-Token")
	if mutating && !csrfOK {
		writeError(response, http.StatusForbidden, "request_rejected")
		return
	}
	principal, err := router.sessions.Authenticate(request.Context(), cookie.Value, csrfToken, mutating)
	if err != nil {
		switch {
		case errors.Is(err, session.ErrForbidden):
			writeError(response, http.StatusForbidden, "request_rejected")
		case errors.Is(err, session.ErrUnauthorized):
			writeError(response, http.StatusUnauthorized, "authentication_failed")
		default:
			writeError(response, http.StatusInternalServerError, "operation_failed")
		}
		return
	}

	switch {
	case request.URL.Path == "/admin/api/session/current":
		if request.Method != http.MethodGet {
			writeMethodNotAllowed(response, http.MethodGet)
			return
		}
		csrfToken, err := router.sessions.RotateCSRF(principal)
		if err != nil {
			if errors.Is(err, session.ErrUnauthorized) {
				writeError(response, http.StatusUnauthorized, "authentication_failed")
			} else {
				writeError(response, http.StatusInternalServerError, "operation_failed")
			}
			return
		}
		writeJSON(response, http.StatusOK, map[string]any{"authenticated": true, "must_change_password": principal.MustChangePassword, "csrf_token": csrfToken})
	case request.URL.Path == "/admin/api/session/logout":
		if request.Method != http.MethodPost {
			writeMethodNotAllowed(response, http.MethodPost)
			return
		}
		router.logout(response, request, principal)
	case request.URL.Path == "/admin/api/session/password":
		if request.Method != http.MethodPost {
			writeMethodNotAllowed(response, http.MethodPost)
			return
		}
		router.changePassword(response, request, principal)
	case principal.MustChangePassword:
		writeError(response, http.StatusForbidden, "password_change_required")
	case request.URL.Path == licenseAdminPrefix || strings.HasPrefix(request.URL.Path, licenseAdminPrefix+"/"):
		router.licenseAdmin(response, request, principal)
	case request.URL.Path == "/admin/api/uploads":
		if request.Method != http.MethodPost {
			writeMethodNotAllowed(response, http.MethodPost)
			return
		}
		router.receiveUpload(response, request, principal, cookie.Value, csrfToken)
	case request.URL.Path == "/admin/api/candidates/current":
		if request.Method != http.MethodGet {
			writeMethodNotAllowed(response, http.MethodGet)
			return
		}
		router.currentCandidate(response, request, principal)
	case request.URL.Path == "/admin/api/releases":
		if request.Method != http.MethodGet {
			writeMethodNotAllowed(response, http.MethodGet)
			return
		}
		router.releaseHistory(response, request)
	case strings.HasPrefix(request.URL.Path, "/admin/api/releases/") && strings.HasSuffix(request.URL.Path, "/rollback"):
		if request.Method != http.MethodPost {
			writeMethodNotAllowed(response, http.MethodPost)
			return
		}
		router.rollbackRelease(response, request, principal)
	case strings.HasPrefix(request.URL.Path, "/admin/api/candidates/") && strings.HasSuffix(request.URL.Path, "/publish"):
		if request.Method != http.MethodPost {
			writeMethodNotAllowed(response, http.MethodPost)
			return
		}
		router.publishCandidate(response, request, principal)
	case strings.HasPrefix(request.URL.Path, "/admin/api/candidates/"):
		if request.Method != http.MethodGet {
			writeMethodNotAllowed(response, http.MethodGet)
			return
		}
		router.candidateStatus(response, request, principal)
	default:
		writeError(response, http.StatusNotFound, "not_found")
	}
}

func writeMethodNotAllowed(response http.ResponseWriter, allowed string) {
	response.Header().Set("Allow", allowed)
	writeError(response, http.StatusMethodNotAllowed, "method_not_allowed")
}
