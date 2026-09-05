package httpapi

import "net/http"

func withAdminSecurityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(response http.ResponseWriter, request *http.Request) {
		response.Header().Set("Cache-Control", "no-store, max-age=0")
		response.Header().Set("Pragma", "no-cache")
		response.Header().Set("Expires", "0")
		response.Header().Set("Strict-Transport-Security", "max-age=31536000; includeSubDomains")
		response.Header().Set("Content-Security-Policy", "default-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'")
		response.Header().Set("X-Content-Type-Options", "nosniff")
		response.Header().Set("X-Frame-Options", "DENY")
		response.Header().Set("Referrer-Policy", "no-referrer")
		response.Header().Set("Permissions-Policy", "camera=(), microphone=(), geolocation=()")
		response.Header().Set("Cross-Origin-Opener-Policy", "same-origin")
		response.Header().Set("Cross-Origin-Resource-Policy", "same-origin")
		next.ServeHTTP(response, request)
	})
}
