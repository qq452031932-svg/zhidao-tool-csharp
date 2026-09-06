package release

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"regexp"
	"strconv"
	"strings"
	"unicode/utf8"
)

const (
	packageID         = "SuperPan.BaiduBatchTransfer"
	channel           = "win-x64-stable"
	manifestName      = "upload-manifest.sha256"
	signatureName     = "upload-manifest.sha256.sig"
	releaseFeedName   = "releases." + channel + ".json"
	assetCatalogName  = "assets." + channel + ".json"
	releasesIndexName = "RELEASES-" + channel
	setupName         = packageID + "-Setup.exe"
	portableName      = packageID + "-Portable.zip"
	maxMetadataBytes  = 1 << 20
)

var (
	canonicalVersion = regexp.MustCompile(`^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$`)
	packageFile      = regexp.MustCompile(`^SuperPan\.BaiduBatchTransfer-((?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4})\.(?:0|[1-9][0-9]{0,4}))-(full|delta)\.nupkg$`)
)

type version struct{ major, minor, patch int }

func parseVersion(value string) (version, error) {
	parts := canonicalVersion.FindStringSubmatch(value)
	if parts == nil {
		return version{}, errors.New("version is not canonical")
	}
	values := [3]int{}
	for index := range values {
		parsed, err := strconv.Atoi(parts[index+1])
		if err != nil || parsed > 65534 {
			return version{}, errors.New("version component is out of range")
		}
		values[index] = parsed
	}
	return version{values[0], values[1], values[2]}, nil
}

func (left version) compare(right version) int {
	if left.major != right.major {
		return left.major - right.major
	}
	if left.minor != right.minor {
		return left.minor - right.minor
	}
	return left.patch - right.patch
}

type releaseDocument struct {
	Assets []releaseAsset `json:"Assets"`
}

type releaseAsset struct {
	PackageID     *string `json:"PackageId"`
	Version       *string `json:"Version"`
	Type          *string `json:"Type"`
	FileName      *string `json:"FileName"`
	SHA1          *string `json:"SHA1"`
	SHA256        *string `json:"SHA256"`
	Size          *int64  `json:"Size"`
	NotesMarkdown *string `json:"NotesMarkdown,omitempty"`
	NotesHTML     *string `json:"NotesHTML,omitempty"`
}

type catalogAsset struct {
	RelativeFileName *string `json:"RelativeFileName"`
	Type             *string `json:"Type"`
}

func (document *releaseDocument) UnmarshalJSON(data []byte) error {
	if err := requireExactObjectKeys(data, "Assets"); err != nil {
		return err
	}
	type alias releaseDocument
	return json.Unmarshal(data, (*alias)(document))
}

func (asset *releaseAsset) UnmarshalJSON(data []byte) error {
	if err := requireExactObjectKeys(data,
		"PackageId", "Version", "Type", "FileName", "SHA1", "SHA256", "Size", "NotesMarkdown", "NotesHTML"); err != nil {
		return err
	}
	type alias releaseAsset
	return json.Unmarshal(data, (*alias)(asset))
}

func (asset *catalogAsset) UnmarshalJSON(data []byte) error {
	if err := requireExactObjectKeys(data, "RelativeFileName", "Type"); err != nil {
		return err
	}
	type alias catalogAsset
	return json.Unmarshal(data, (*alias)(asset))
}

func requireExactObjectKeys(data []byte, allowed ...string) error {
	var object map[string]json.RawMessage
	if err := json.Unmarshal(data, &object); err != nil || object == nil {
		return errors.New("JSON value is not an object")
	}
	accepted := make(map[string]struct{}, len(allowed))
	for _, key := range allowed {
		accepted[key] = struct{}{}
	}
	for key := range object {
		if _, ok := accepted[key]; !ok {
			return fmt.Errorf("JSON property %q does not use its exact schema spelling", key)
		}
	}
	return nil
}

func decodeStrictJSON(data []byte, destination any) error {
	if len(data) == 0 || len(data) > maxMetadataBytes || !utf8.Valid(data) || bytes.HasPrefix(data, []byte{0xef, 0xbb, 0xbf}) {
		return errors.New("JSON size or encoding is invalid")
	}
	if err := rejectDuplicateJSONKeys(data); err != nil {
		return err
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(destination); err != nil {
		return err
	}
	if err := decoder.Decode(new(any)); !errors.Is(err, io.EOF) {
		return errors.New("JSON contains trailing data")
	}
	return nil
}

type jsonFrame struct {
	object       bool
	expectingKey bool
	seen         map[string]struct{}
}

func rejectDuplicateJSONKeys(data []byte) error {
	decoder := json.NewDecoder(bytes.NewReader(data))
	stack := make([]jsonFrame, 0, 8)
	completeValue := func() {
		if len(stack) > 0 && stack[len(stack)-1].object {
			stack[len(stack)-1].expectingKey = true
		}
	}
	for {
		token, err := decoder.Token()
		if errors.Is(err, io.EOF) {
			return nil
		}
		if err != nil {
			return err
		}
		if delimiter, ok := token.(json.Delim); ok {
			switch delimiter {
			case '{':
				stack = append(stack, jsonFrame{object: true, expectingKey: true, seen: map[string]struct{}{}})
			case '[':
				stack = append(stack, jsonFrame{})
			case '}', ']':
				if len(stack) == 0 {
					return errors.New("JSON delimiter is unbalanced")
				}
				stack = stack[:len(stack)-1]
				completeValue()
			}
			continue
		}
		if len(stack) > 0 && stack[len(stack)-1].object && stack[len(stack)-1].expectingKey {
			key, ok := token.(string)
			if !ok {
				return errors.New("JSON object key is invalid")
			}
			for prior := range stack[len(stack)-1].seen {
				if strings.EqualFold(prior, key) {
					return fmt.Errorf("duplicate JSON property %q", key)
				}
			}
			stack[len(stack)-1].seen[key] = struct{}{}
			stack[len(stack)-1].expectingKey = false
			continue
		}
		completeValue()
	}
}

func exactHex(value string, length int, upper bool) bool {
	if len(value) != length {
		return false
	}
	for _, character := range value {
		if !(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f') && !(character >= 'A' && character <= 'F') {
			return false
		}
	}
	if upper {
		return value == strings.ToUpper(value)
	}
	return value == strings.ToLower(value)
}
