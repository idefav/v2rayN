// udpgw-deploy is uploaded over pinned SFTP and runs only on the target Linux.
package main

import (
	"context"
	"encoding/json"
	"io"
	"os"
	"runtime"
	"time"
	"v2rayn/proxy-check/internal/gatewaydeploy"
)

func main() {
	if len(os.Args) == 2 && os.Args[1] == "--version" {
		println("v2rayn-udpgw-deploy 1")
		return
	}
	out := gatewaydeploy.Response{Version: 1, State: "failed"}
	defer func() { _ = json.NewEncoder(os.Stdout).Encode(out) }()
	if runtime.GOOS != "linux" || os.Geteuid() != 0 {
		out.Error = "E_LINUX_ROOT_REQUIRED"
		return
	}
	r := gatewaydeploy.Request{}
	if len(os.Args) == 3 && os.Args[1] == "--rollback" {
		r.Version = 1
		r.Action = "rollback"
		r.TransactionID = os.Args[2]
	} else {
		d := json.NewDecoder(io.LimitReader(os.Stdin, 16385))
		d.DisallowUnknownFields()
		if d.Decode(&r) != nil {
			out.Error = "E_REQUEST"
			return
		}
	}
	ctx, cancel := context.WithTimeout(context.Background(), 120*time.Second)
	defer cancel()
	out = gatewaydeploy.New().Execute(ctx, r, os.Stdout)
}
