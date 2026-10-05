package xlang;

import boofcv.abst.fiducial.MicroQrCodeDetector;
import boofcv.abst.fiducial.QrCodeDetector;
import boofcv.factory.fiducial.FactoryFiducial;
import boofcv.struct.image.GrayU8;

/**
 * BoofCV's CLI for the cross-language benchmark: Standard QR and Micro QR image decode.
 * Each symbology has its own detector, built once with its default configuration and reused for every image, as a caller
 * reading frames uses it. BoofCV reads neither rMQR nor a module matrix through its API, and its encoders are not measured.
 */
public final class BoofCvCli {
    private BoofCvCli() {
    }

    public static void main(String[] args) {
        System.exit(Protocol.run(args, "BoofCV", BoofCvCli::load));
    }

    private static Protocol.Operation load(String op, String symbology, String input, String ecc, String version) throws Exception {
        if (!op.equals("decode-image") || !(symbology.equals("qr") || symbology.equals("microqr")))
            return null;
        Protocol.Image pgm = Protocol.readPgm(input);
        GrayU8 image = new GrayU8(pgm.width(), pgm.height());
        System.arraycopy(pgm.pixels(), 0, image.data, 0, pgm.pixels().length);
        Protocol.Input<GrayU8> holder = new Protocol.Input<>(image);

        if (symbology.equals("qr")) {
            QrCodeDetector<GrayU8> detector = FactoryFiducial.qrcode(null, GrayU8.class);
            return new Protocol.Operation(
                    () -> {
                        detector.process(holder.value);
                        var found = detector.getDetections();
                        return found.isEmpty() ? 0 : Protocol.fold(found.get(0).message);
                    },
                    () -> {
                        detector.process(image);
                        var found = detector.getDetections();
                        return found.isEmpty() ? Protocol.FAILED : Protocol.decoded(found.get(0).message);
                    });
        }

        MicroQrCodeDetector<GrayU8> detector = FactoryFiducial.microqr(null, GrayU8.class);
        return new Protocol.Operation(
                () -> {
                    detector.process(holder.value);
                    var found = detector.getDetections();
                    return found.isEmpty() ? 0 : Protocol.fold(found.get(0).message);
                },
                () -> {
                    detector.process(image);
                    var found = detector.getDetections();
                    return found.isEmpty() ? Protocol.FAILED : Protocol.decoded(found.get(0).message);
                });
    }
}
