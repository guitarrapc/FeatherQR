// gozxing's CLI for the cross-language benchmark: Standard QR encode and decode through gozxing, the Go port of ZXing, whose original
// zxing-java runs through the same calls. gozxing reads and writes neither Micro QR nor rMQR, so those report unsupported.
//
//   - encode: encoder.Encoder_encode to the module matrix, the encoder behind its QRCodeWriter without the scaling to pixels, pinned to
//     the level and version. gozxing's default character set is UTF-8 rather than ZXing's ISO-8859-1, so the text goes as UTF-8 with
//     no hint and no ECI, as a caller's would.
//   - decode-matrix: decoder.Decode over a BitMatrix of the bare symbol. Like ZXing's, it unmasks the matrix it is given in place and
//     leaves it unmasked when it succeeds, so each call decodes its own copy, made with BitMatrix.Xor onto an empty matrix since
//     gozxing has no Clone. The copy, a few dozen words, is part of the call.
//   - decode-image: QRCodeReader over the grey pixels as a luminance plane through HybridBinarizer, with no hints.
package main

import (
	"errors"
	"os"
	"strconv"

	"github.com/makiuchi-d/gozxing"
	"github.com/makiuchi-d/gozxing/qrcode"
	"github.com/makiuchi-d/gozxing/qrcode/decoder"
	"github.com/makiuchi-d/gozxing/qrcode/encoder"

	"xlang-go/protocol"
)

func main() {
	os.Exit(protocol.Run("gozxing", "github.com/makiuchi-d/gozxing", load))
}

func load(args *protocol.Args) (*protocol.Operation, error) {
	if args.Symbology != "qr" {
		return nil, nil
	}
	switch args.Op {
	case "encode":
		text, err := protocol.ReadText(args.Input)
		if err != nil {
			return nil, err
		}
		levels := map[string]decoder.ErrorCorrectionLevel{"L": decoder.ErrorCorrectionLevel_L, "M": decoder.ErrorCorrectionLevel_M, "Q": decoder.ErrorCorrectionLevel_Q, "H": decoder.ErrorCorrectionLevel_H}
		level, ok := levels[args.Ecc]
		version, err := strconv.Atoi(args.Version)
		if !ok || err != nil {
			return nil, errors.New("encode needs --ecc L, M, Q or H and --version 1 to 40")
		}
		hints := map[gozxing.EncodeHintType]interface{}{gozxing.EncodeHintType_QR_VERSION: version}
		return &protocol.Operation{
			Call: func() uint64 {
				code, err := encoder.Encoder_encode(text, level, hints)
				if err != nil {
					return 0
				}
				// The matrix size and its centre module, so the fold depends on the content.
				matrix := code.GetMatrix()
				return uint64(matrix.GetWidth())*2 + uint64(matrix.Get(matrix.GetWidth()/2, matrix.GetHeight()/2))
			},
			Describe: func() string {
				code, err := encoder.Encoder_encode(text, level, hints)
				if err != nil {
					return protocol.Failed
				}
				matrix := code.GetMatrix()
				return protocol.Matrix(matrix.GetWidth(), matrix.GetHeight(), func(row, col int) bool { return matrix.Get(col, row) == 1 })
			},
		}, nil
	case "decode-matrix":
		pgm, err := protocol.ReadPgm(args.Input)
		if err != nil {
			return nil, err
		}
		bits, err := gozxing.NewBitMatrix(pgm.Width, pgm.Height)
		if err != nil {
			return nil, err
		}
		for y := 0; y < pgm.Height; y++ {
			for x := 0; x < pgm.Width; x++ {
				if pgm.Pixels[y*pgm.Width+x] < 128 {
					bits.Set(x, y)
				}
			}
		}
		qrDecoder := decoder.NewDecoder()
		decode := func() (string, bool) {
			copied, _ := gozxing.NewBitMatrix(bits.GetWidth(), bits.GetHeight())
			if copied.Xor(bits) != nil {
				return "", false
			}
			result, err := qrDecoder.Decode(copied, nil)
			if err != nil {
				return "", false
			}
			return result.GetText(), true
		}
		return &protocol.Operation{
			Call: func() uint64 {
				text, ok := decode()
				if !ok {
					return 0
				}
				return protocol.FoldText(text)
			},
			Describe: func() string {
				text, ok := decode()
				if !ok {
					return protocol.Failed
				}
				return protocol.Decoded(text)
			},
		}, nil
	case "decode-image":
		pgm, err := protocol.ReadPgm(args.Input)
		if err != nil {
			return nil, err
		}
		reader := qrcode.NewQRCodeReader()
		decode := func() (string, bool) {
			source, err := gozxing.NewPlanarYUVLuminanceSource(pgm.Pixels, pgm.Width, pgm.Height, 0, 0, pgm.Width, pgm.Height, false)
			if err != nil {
				return "", false
			}
			bitmap, err := gozxing.NewBinaryBitmap(gozxing.NewHybridBinarizer(source))
			if err != nil {
				return "", false
			}
			result, err := reader.Decode(bitmap, nil)
			if err != nil {
				return "", false
			}
			return result.GetText(), true
		}
		return &protocol.Operation{
			Call: func() uint64 {
				text, ok := decode()
				if !ok {
					return 0
				}
				return protocol.FoldText(text)
			},
			Describe: func() string {
				text, ok := decode()
				if !ok {
					return protocol.Failed
				}
				return protocol.Decoded(text)
			},
		}, nil
	}
	return nil, nil
}
