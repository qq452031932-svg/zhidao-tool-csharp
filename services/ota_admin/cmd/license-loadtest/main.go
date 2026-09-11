package main

import (
 "errors"
 "flag"
 "fmt"
 "net"
 "net/url"
 "os"
 "strings"
)

type Report struct { Requests, Successes, Rejections, Errors int64; P95Millis float64 }
func (r Report) String() string { return fmt.Sprintf("requests=%d successes=%d rejections=%d errors=%d p95_ms=%.2f", r.Requests,r.Successes,r.Rejections,r.Errors,r.P95Millis) }
func validateOrigin(origin string) error {
 u, err := url.Parse(origin); if err != nil || u.Scheme != "http" || u.Path != "" || u.RawQuery != "" || u.Fragment != "" { return errors.New("origin must be loopback http URL") }
 host, port, err := net.SplitHostPort(u.Host); if err != nil || port == "" { return errors.New("origin must include port") }
 ip := net.ParseIP(host); if host != "localhost" && (ip == nil || !ip.IsLoopback()) { return errors.New("origin host must be loopback") }; return nil
}
func main() { origin:=flag.String("origin","", "loopback origin"); flag.Parse(); if err:=validateOrigin(*origin); err!=nil { fmt.Fprintln(os.Stderr,err); os.Exit(2) }; fmt.Println((Report{}).String()) }
var _ = strings.TrimSpace
