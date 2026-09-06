package candidate

import (
	"errors"
	"os"
	"path/filepath"
)

func candidatePathNames(id string) (string, string, error) {
	if !canonicalUUID.MatchString(id) {
		return "", "", errors.New("candidate ID is invalid")
	}
	base := ".candidate-" + id
	return base + ".zip", base, nil
}

func candidatePathsPresent(root, id string) (bool, error) {
	archiveName, extractionName, err := candidatePathNames(id)
	if err != nil {
		return false, err
	}
	for _, name := range []string{archiveName, extractionName} {
		_, err := os.Lstat(filepath.Join(root, name))
		if err == nil {
			return true, nil
		}
		if !errors.Is(err, os.ErrNotExist) {
			return false, err
		}
	}
	return false, nil
}
