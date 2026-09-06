package candidate

import "strings"

func candidateIDFromArtifact(name string) (string, bool) {
	if !strings.HasPrefix(name, ".candidate-") {
		return "", false
	}
	id := strings.TrimPrefix(name, ".candidate-")
	if strings.HasSuffix(id, ".zip") {
		id = strings.TrimSuffix(id, ".zip")
	}
	return id, canonicalUUID.MatchString(id)
}
