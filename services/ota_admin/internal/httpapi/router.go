package httpapi

import (
	"errors"
	"net/http"
	"strings"

	"fanfh.cn/superpan/ota-admin/internal/security"
	"fanfh.cn/superpan/ota-admin/internal/session"
)

const (
	preauthCookieName = "ota_admin_preauth"
	sessionCookieName = "ota_admin_session"
)

type Options struct {
	Sessions     *session.Service
	PublicOrigin string
	MaxJSONBytes int64
}

type Router struct {
	sessions     *session.Service
	publicOrigin string
	maxJSONBytes int64
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
	router := &Router{sessions: options.Sessions, publicOrigin: options.PublicOrigin, maxJSONBytes: options.MaxJSONBytes}
	return withAdminSecurityHeaders(router), nil
}

func (router *Router) ServeHTTP(response http.ResponseWriter, request *http.Request) {
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

func (router *Router) authenticated(response http.ResponseWriter, request *http.Request) {
	mutating := request.Method == http.MethodPost || request.Method == http.MethodPut || request.Method == http.MethodPatch || request.Method == http.MethodDelete
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
		if errors.Is(err, session.ErrForbidden) {
			writeError(response, http.StatusForbidden, "request_rejected")
		} else {
			writeError(response, http.StatusUnauthorized, "authentication_failed")
		}
		return
	}

	switch {
	case request.URL.Path == "/admin/api/session/current" && request.Method == http.MethodGet:
		writeJSON(response, http.StatusOK, map[string]any{"authenticated": true, "must_change_password": principal.MustChangePassword})
	case request.URL.Path == "/admin/api/session/logout" && request.Method == http.MethodPost:
		router.logout(response, request, principal)
	case request.URL.Path == "/admin/api/session/password" && request.Method == http.MethodPost:
		router.changePassword(response, request, principal)
	case principal.MustChangePassword:
		writeError(response, http.StatusForbidden, "password_change_required")
	default:
		writeError(response, http.StatusNotFound, "not_found")
	}
}
