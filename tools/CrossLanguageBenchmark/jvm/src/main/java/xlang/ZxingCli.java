package xlang;

import com.google.zxing.BinaryBitmap;
import com.google.zxing.EncodeHintType;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.ReaderException;
import com.google.zxing.WriterException;
import com.google.zxing.common.BitMatrix;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.qrcode.QRCodeReader;
import com.google.zxing.qrcode.decoder.Decoder;
import com.google.zxing.qrcode.decoder.ErrorCorrectionLevel;
import com.google.zxing.qrcode.encoder.ByteMatrix;
import com.google.zxing.qrcode.encoder.Encoder;
import com.google.zxing.qrcode.encoder.QRCode;
import java.nio.charset.StandardCharsets;
import java.util.EnumMap;
import java.util.Map;

/**
 * ZXing's CLI for the cross-language benchmark: Standard QR encode and decode. ZXing reads and writes neither Micro QR nor rMQR.
 *
 * <ul>
 * <li>encode: {@code Encoder.encode} to the module matrix, the encoder behind {@code QRCodeWriter} without its scaling to
 * pixels, pinned to the level and version. Text ISO-8859-1 cannot hold goes as UTF-8 with an ECI, as a caller sets it.</li>
 * <li>decode-matrix: {@code Decoder.decode} over a {@code BitMatrix} of the bare symbol.</li>
 * <li>decode-image: {@code QRCodeReader}, the Standard QR reader, over the grey pixels as a luminance plane through
 * {@code HybridBinarizer}, the binarizer ZXing's own clients use, with no hints.</li>
 * </ul>
 */
public final class ZxingCli {
    private ZxingCli() {
    }

    public static void main(String[] args) {
        System.exit(Protocol.run(args, "ZXing", ZxingCli::load));
    }

    private static Protocol.Operation load(String op, String symbology, String input, String ecc, String version) throws Exception {
        if (!symbology.equals("qr"))
            return null;
        return switch (op) {
            case "encode" -> encode(Protocol.readText(input), ErrorCorrectionLevel.valueOf(ecc), Integer.parseInt(version));
            case "decode-matrix" -> decodeMatrix(Protocol.readPgm(input));
            case "decode-image" -> decodeImage(Protocol.readPgm(input));
            default -> null;
        };
    }

    private static Protocol.Operation encode(String text, ErrorCorrectionLevel level, int version) {
        Map<EncodeHintType, Object> hints = new EnumMap<>(EncodeHintType.class);
        hints.put(EncodeHintType.QR_VERSION, version);
        if (!StandardCharsets.ISO_8859_1.newEncoder().canEncode(text))
            hints.put(EncodeHintType.CHARACTER_SET, "UTF-8");
        Protocol.Input<String> holder = new Protocol.Input<>(text);
        return new Protocol.Operation(
                () -> {
                    ByteMatrix matrix = encode(holder.value, level, hints).getMatrix();
                    // The matrix size and its centre module, so the fold depends on the content.
                    return matrix.getWidth() * 2L + matrix.get(matrix.getWidth() / 2, matrix.getHeight() / 2);
                },
                () -> {
                    try {
                        ByteMatrix matrix = Encoder.encode(text, level, hints).getMatrix();
                        return Protocol.matrix(matrix.getWidth(), matrix.getHeight(), (row, col) -> matrix.get(col, row) == 1);
                    } catch (WriterException e) {
                        return Protocol.FAILED;
                    }
                });
    }

    private static QRCode encode(String text, ErrorCorrectionLevel level, Map<EncodeHintType, Object> hints) {
        try {
            return Encoder.encode(text, level, hints);
        } catch (WriterException e) {
            throw new IllegalStateException(e);
        }
    }

    private static Protocol.Operation decodeMatrix(Protocol.Image pgm) {
        BitMatrix bits = new BitMatrix(pgm.width(), pgm.height());
        for (int y = 0; y < pgm.height(); y++)
            for (int x = 0; x < pgm.width(); x++)
                if ((pgm.pixels()[y * pgm.width() + x] & 0xFF) < 128)
                    bits.set(x, y);
        Decoder decoder = new Decoder();
        Protocol.Input<BitMatrix> holder = new Protocol.Input<>(bits);
        return new Protocol.Operation(
                () -> {
                    try {
                        return Protocol.fold(decoder.decode(holder.value).getText());
                    } catch (ReaderException e) {
                        return 0;
                    }
                },
                () -> {
                    try {
                        return Protocol.decoded(decoder.decode(bits).getText());
                    } catch (ReaderException e) {
                        return Protocol.FAILED;
                    }
                });
    }

    private static Protocol.Operation decodeImage(Protocol.Image pgm) {
        QRCodeReader reader = new QRCodeReader();
        Protocol.Input<byte[]> holder = new Protocol.Input<>(pgm.pixels());
        int width = pgm.width(), height = pgm.height();
        return new Protocol.Operation(
                () -> {
                    try {
                        var source = new PlanarYUVLuminanceSource(holder.value, width, height, 0, 0, width, height, false);
                        return Protocol.fold(reader.decode(new BinaryBitmap(new HybridBinarizer(source))).getText());
                    } catch (ReaderException e) {
                        return 0;
                    }
                },
                () -> {
                    try {
                        var source = new PlanarYUVLuminanceSource(pgm.pixels(), width, height, 0, 0, width, height, false);
                        return Protocol.decoded(reader.decode(new BinaryBitmap(new HybridBinarizer(source))).getText());
                    } catch (ReaderException e) {
                        return Protocol.FAILED;
                    }
                });
    }
}
