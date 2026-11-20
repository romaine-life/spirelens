package main

import (
	"fmt"
	"io"
)

// Stamped by the release suite's Go build through .goreleaser.yaml's ldflags. The linker
// ignores an -X naming a variable that doesn't exist, so version_test.go holds the
// names together.
var (
	version     string // the release tag
	buildRepo   string // GITHUB_REPOSITORY, owner/name
	buildServer string // GITHUB_SERVER_URL
	buildTime   string // UTC, RFC 3339
)

func printVersion(w io.Writer) {
	if version == "" {
		fmt.Fprintln(w, "spirelens-db development build, not from CI")
		return
	}
	fmt.Fprintf(w, "spirelens-db %s\n", version)
	if buildRepo != "" {
		fmt.Fprintf(w, "repo   %s/%s\n", buildServer, buildRepo)
	}
	if buildTime != "" {
		fmt.Fprintf(w, "built  %s\n", buildTime)
	}
}
