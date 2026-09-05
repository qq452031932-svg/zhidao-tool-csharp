package release

import (
	"errors"
	"strconv"
	"strings"
)

type releasesEntry struct {
	SHA1 string
	Name string
	Size int64
}

func parseReleasesIndex(data []byte) ([]releasesEntry, error) {
	if len(data) == 0 || len(data) > maxMetadataBytes || data[len(data)-1] != '\n' ||
		(len(data) > 1 && data[len(data)-2] == '\n') || strings.ContainsRune(string(data), '\r') {
		return nil, errors.New("RELEASES is not canonical")
	}
	lines := strings.Split(string(data[:len(data)-1]), "\n")
	entries := make([]releasesEntry, 0, len(lines))
	for _, line := range lines {
		parts := strings.Split(line, " ")
		if len(parts) != 3 || !exactHex(parts[0], 40, true) || parts[1] == "" ||
			(parts[2] != "0" && (strings.HasPrefix(parts[2], "0") || strings.HasPrefix(parts[2], "+"))) {
			return nil, errors.New("RELEASES contains a non-canonical line")
		}
		size, err := strconv.ParseInt(parts[2], 10, 64)
		if err != nil || size < 0 {
			return nil, errors.New("RELEASES size is invalid")
		}
		entries = append(entries, releasesEntry{SHA1: parts[0], Name: parts[1], Size: size})
	}
	return entries, nil
}
