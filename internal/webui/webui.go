// Package webui embeds the compiled single page front end and serves it.
package webui

import (
	"embed"
	"io/fs"
	"net/http"
	"path"
	"strings"
)

// The dist directory is produced by `npm run build` in web/. A placeholder
// index.html is checked in so that `go build ./...` and `go test ./...` work
// before the front end has ever been built.
//
//go:embed all:dist
var distFS embed.FS

// Handler serves the embedded front end, falling back to index.html so that the
// single page application answers unknown paths.
func Handler() (http.Handler, error) {
	sub, err := fs.Sub(distFS, "dist")
	if err != nil {
		return nil, err
	}
	files := http.FileServerFS(sub)

	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet && r.Method != http.MethodHead {
			w.Header().Set("Allow", "GET, HEAD")
			http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
			return
		}

		name := strings.TrimPrefix(path.Clean("/"+r.URL.Path), "/")
		if name == "" {
			serveIndex(w, r, files)
			return
		}

		if f, err := sub.Open(name); err == nil {
			_ = f.Close()
			if strings.HasPrefix(name, "assets/") {
				// Vite fingerprints asset file names, so they are immutable.
				w.Header().Set("Cache-Control", "public, max-age=31536000, immutable")
			} else if strings.HasSuffix(name, ".html") {
				w.Header().Set("Cache-Control", "no-cache")
			}
			files.ServeHTTP(w, r)
			return
		}

		// Unknown path without a file extension: hand it to the SPA. Anything that
		// looks like a static asset really is missing.
		if path.Ext(name) != "" {
			http.NotFound(w, r)
			return
		}
		serveIndex(w, r, files)
	}), nil
}

func serveIndex(w http.ResponseWriter, r *http.Request, files http.Handler) {
	w.Header().Set("Cache-Control", "no-cache")
	clone := r.Clone(r.Context())
	clone.URL.Path = "/"
	files.ServeHTTP(w, clone)
}
