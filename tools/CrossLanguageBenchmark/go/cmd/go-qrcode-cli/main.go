// go-qrcode's CLI for the cross-language benchmark: Standard QR encode, pinned to the case's level and version. go-qrcode writes neither
// Micro QR nor rMQR and does not decode, so the rest report unsupported.
//
// The timed call is NewWithForcedVersion, which splits the text into segments, then Bitmap, the module matrix with go-qrcode's 4-module
// quiet zone, which is where it adds the error correction and picks the mask. It holds no ECI and writes the text's UTF-8 bytes. The
// recovery levels Low, Medium, High and Highest are L, M, Q and H.
package main

import (
	"errors"
	"os"
	"strconv"

	qrcode "github.com/skip2/go-qrcode"

	"xlang-go/protocol"
)

func main() {
	os.Exit(protocol.Run("go-qrcode", "github.com/skip2/go-qrcode", load))
}

func load(args *protocol.Args) (*protocol.Operation, error) {
	if args.Op != "encode" || args.Symbology != "qr" {
		return nil, nil
	}
	text, err := protocol.ReadText(args.Input)
	if err != nil {
		return nil, err
	}
	levels := map[string]qrcode.RecoveryLevel{"L": qrcode.Low, "M": qrcode.Medium, "Q": qrcode.High, "H": qrcode.Highest}
	level, ok := levels[args.Ecc]
	version, err := strconv.Atoi(args.Version)
	if !ok || err != nil {
		return nil, errors.New("encode needs --ecc L, M, Q or H and --version 1 to 40")
	}
	encode := func() [][]bool {
		code, err := qrcode.NewWithForcedVersion(text, version, level)
		if err != nil {
			return nil
		}
		return code.Bitmap()
	}
	return &protocol.Operation{
		Call: func() uint64 {
			bitmap := encode()
			if bitmap == nil {
				return 0
			}
			// The bitmap's size and its centre module, so the fold depends on the content.
			size := len(bitmap)
			centre := uint64(0)
			if bitmap[size/2][size/2] {
				centre = 1
			}
			return uint64(size)*2 + centre
		},
		Describe: func() string {
			bitmap := encode()
			if bitmap == nil {
				return protocol.Failed
			}
			return protocol.Matrix(len(bitmap), len(bitmap), func(row, col int) bool { return bitmap[row][col] })
		},
	}, nil
}
