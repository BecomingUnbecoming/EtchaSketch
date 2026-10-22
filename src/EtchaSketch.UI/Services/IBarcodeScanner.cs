using System.Threading.Tasks;

namespace EtchaSketch.UI.Services;

/// <summary>Scans a barcode with the device's camera, where the platform can.</summary>
public interface IBarcodeScanner
{
    /// <summary>The barcode's text, or null when the person backed out.</summary>
    /// <exception cref="BarcodeScannerMissingException">No scanner app is installed.</exception>
    Task<string?> ScanAsync();
}

/// <summary>The device has nothing that can scan barcodes for the app.</summary>
public sealed class BarcodeScannerMissingException() : System.Exception("No barcode scanner app is installed.");
