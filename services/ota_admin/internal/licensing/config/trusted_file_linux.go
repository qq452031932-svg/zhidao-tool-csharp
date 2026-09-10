//go:build linux

package config

import (
	"errors"
	"io"
	"os"
	"os/user"
	"path/filepath"
	"strconv"
	"strings"

	"golang.org/x/sys/unix"
)

const licenseRuntimeGroup = "superpan-license"

type trustedFilePolicy struct {
	allowedGIDs  map[uint32]struct{}
	exactSize    int64
	maximumSize  int64
	allowedModes map[os.FileMode]struct{}
}

type trustedPathNode struct {
	parent int
	name   string
	dev    uint64
	ino    uint64
}

func readConfigFile(path string, maximumSize int64) ([]byte, error) {
	return readLicenseFile(path, 0, maximumSize)
}

func readLicenseFile(path string, exactSize, maximumSize int64) ([]byte, error) {
	group, err := user.LookupGroup(licenseRuntimeGroup)
	if err != nil {
		return nil, errors.New("license runtime group is unavailable")
	}
	gid, err := strconv.ParseUint(group.Gid, 10, 32)
	if err != nil {
		return nil, errors.New("license runtime group is invalid")
	}
	return readTrustedFile(path, trustedFilePolicy{allowedGIDs: map[uint32]struct{}{uint32(gid): {}}, exactSize: exactSize, maximumSize: maximumSize, allowedModes: map[os.FileMode]struct{}{0o600: {}, 0o640: {}}}, nil)
}

func readSharedFile(path string, exactSize, maximumSize int64) ([]byte, error) {
	groups, err := os.Getgroups()
	if err != nil {
		return nil, errors.New("exactly one supplementary group is required")
	}
	policy, err := sharedFilePolicy(groups, exactSize, maximumSize)
	if err != nil {
		return nil, err
	}
	return readTrustedFile(path, policy, nil)
}

func readTrustedFile(path string, policy trustedFilePolicy, afterOpen func()) ([]byte, error) {
	if path == "" || !filepath.IsAbs(path) || filepath.Clean(path) != path || policy.maximumSize < 1 || policy.exactSize < 0 || policy.exactSize > policy.maximumSize || len(policy.allowedGIDs) == 0 || len(policy.allowedModes) == 0 {
		return nil, errors.New("trusted file request is invalid")
	}
	root, err := unix.Open("/", unix.O_RDONLY|unix.O_DIRECTORY|unix.O_CLOEXEC|unix.O_NOFOLLOW, 0)
	if err != nil {
		return nil, errors.New("open trusted path root")
	}
	fds := []int{root}
	defer func() {
		for index := len(fds) - 1; index >= 0; index-- {
			if fds[index] >= 0 {
				_ = unix.Close(fds[index])
			}
		}
	}()
	var rootStat unix.Stat_t
	if err := unix.Fstat(root, &rootStat); err != nil || rootStat.Uid != 0 || rootStat.Mode&unix.S_IFMT != unix.S_IFDIR || rootStat.Mode&0o022 != 0 {
		return nil, errors.New("trusted path root is unsafe")
	}

	components := strings.Split(strings.TrimPrefix(path, "/"), "/")
	if len(components) == 0 || components[0] == "" {
		return nil, errors.New("trusted file path is invalid")
	}
	nodes := make([]trustedPathNode, 0, len(components))
	parent := root
	var fileStat unix.Stat_t
	for index, component := range components {
		if component == "" || component == "." || component == ".." {
			return nil, errors.New("trusted file path is invalid")
		}
		flags := unix.O_RDONLY | unix.O_CLOEXEC | unix.O_NOFOLLOW
		if index < len(components)-1 {
			flags |= unix.O_DIRECTORY
		} else {
			flags |= unix.O_NONBLOCK
		}
		fd, err := unix.Openat(parent, component, flags, 0)
		if err != nil {
			return nil, errors.New("open trusted path component")
		}
		fds = append(fds, fd)
		var stat unix.Stat_t
		if err := unix.Fstat(fd, &stat); err != nil {
			return nil, errors.New("inspect trusted path component")
		}
		nodes = append(nodes, trustedPathNode{parent: parent, name: component, dev: uint64(stat.Dev), ino: stat.Ino})
		if stat.Uid != 0 {
			return nil, errors.New("trusted path owner is invalid")
		}
		if index < len(components)-1 {
			if stat.Mode&unix.S_IFMT != unix.S_IFDIR || stat.Mode&0o022 != 0 {
				return nil, errors.New("trusted parent directory is unsafe")
			}
			parent = fd
			continue
		}
		fileStat = stat
	}
	if fileStat.Mode&unix.S_IFMT != unix.S_IFREG || fileStat.Nlink != 1 {
		return nil, errors.New("trusted file type or link count is invalid")
	}
	if _, ok := policy.allowedGIDs[fileStat.Gid]; !ok {
		return nil, errors.New("trusted file group is invalid")
	}
	if _, ok := policy.allowedModes[os.FileMode(fileStat.Mode&0o777)]; !ok {
		return nil, errors.New("trusted file mode is invalid")
	}
	if fileStat.Size < 0 || fileStat.Size > policy.maximumSize || (policy.exactSize > 0 && fileStat.Size != policy.exactSize) {
		return nil, errors.New("trusted file size is invalid")
	}
	if afterOpen != nil {
		afterOpen()
	}
	file := os.NewFile(uintptr(fds[len(fds)-1]), "trusted-file")
	if file == nil {
		return nil, errors.New("open trusted file descriptor")
	}
	fds[len(fds)-1] = -1
	defer file.Close()
	data, err := io.ReadAll(io.LimitReader(file, policy.maximumSize+1))
	if err != nil || int64(len(data)) > policy.maximumSize || (policy.exactSize > 0 && int64(len(data)) != policy.exactSize) {
		clear(data)
		return nil, errors.New("read trusted file")
	}
	for _, node := range nodes {
		var current unix.Stat_t
		if err := unix.Fstatat(node.parent, node.name, &current, unix.AT_SYMLINK_NOFOLLOW); err != nil || uint64(current.Dev) != node.dev || current.Ino != node.ino {
			clear(data)
			return nil, errors.New("trusted path changed during open")
		}
	}
	return data, nil
}
