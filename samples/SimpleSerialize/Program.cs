using FeatherQR;
using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var qrCode = QRCodeGenerator.Create("https://example.com/foobar", QREccLevel.M, new QRCodeGeneratorOptions { QuietZoneSize = 4 });

// serialized
var serialized = qrCode.GetRawData();
File.WriteAllBytes("qrcode.qrr", serialized);

// deserialized
var loaded = File.ReadAllBytes("qrcode.qrr");
var restored = new QRCodeData(loaded, quietZoneSize: 4);

// draw on console
Debug.Assert(qrCode.Size == restored.Size, "Original data and restored data must match.");
for (var row = 0; row < qrCode.Size; row++)
{
    for (var col = 0; col < qrCode.Size; col++)
    {
        Debug.Assert(qrCode[row, col] == restored[row, col], "Original data and restored data must match.");
        Console.Write(qrCode[row, col] ? "⚪" : "  ");
    }
    Console.Write("\n");
}

Console.WriteLine("Serialization and deserialization completed successfully.");
