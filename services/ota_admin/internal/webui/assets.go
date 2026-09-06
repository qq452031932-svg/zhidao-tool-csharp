package webui

import (
	"embed"
	"net/http"
)

//go:embed static/index.html static/styles.css static/app.js
var staticFiles embed.FS

type assetHandler struct{}

func Handler() http.Handler { return assetHandler{} }

func (assetHandler) ServeHTTP(response http.ResponseWriter, request *http.Request) {
	if request.Method != http.MethodGet {
		response.WriteHeader(http.StatusNotFound)
		return
	}
	var name, contentType string
	switch request.URL.Path {
	case "/admin/", "/admin/index.html":
		name, contentType = "static/index.html", "text/html; charset=utf-8"
	case "/admin/styles.css":
		name, contentType = "static/styles.css", "text/css; charset=utf-8"
	case "/admin/app.js":
		name, contentType = "static/app.js", "text/javascript; charset=utf-8"
	default:
		response.WriteHeader(http.StatusNotFound)
		return
	}
	content, err := staticFiles.ReadFile(name)
	if err != nil {
		response.WriteHeader(http.StatusInternalServerError)
		return
	}
	response.Header().Set("Content-Type", contentType)
	response.WriteHeader(http.StatusOK)
	_, _ = response.Write(content)
}
