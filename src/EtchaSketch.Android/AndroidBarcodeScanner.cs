using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using EtchaSketch.UI.Services;

namespace EtchaSketch.Android;

/// <summary>
/// Scans through a barcode scanner app the person already has, such as Binary Eye (free and open
/// source, on F-Droid and Accrescent), using the scan request those apps share. EtchaSketch then
/// needs no camera permission of its own, and no Google services.
/// </summary>
public sealed class AndroidBarcodeScanner : IBarcodeScanner
{
    private const string ScanAction = "com.google.zxing.client.android.SCAN";
    private const int RequestCode = 0x5CA7;

    private static TaskCompletionSource<string?>? _pending;

    public Task<string?> ScanAsync()
    {
        if (AndroidDeviceUnlock.CurrentActivity is not { } activity)
        {
            return Task.FromResult<string?>(null);
        }

        var intent = new Intent(ScanAction);
        intent.PutExtra("SCAN_FORMATS", "EAN_13,EAN_8,UPC_A,UPC_E");
        _pending?.TrySetResult(null);
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = pending;
        try
        {
            activity.StartActivityForResult(intent, RequestCode);
        }
        catch (ActivityNotFoundException)
        {
            _pending = null;
            throw new BarcodeScannerMissingException();
        }

        return pending.Task;
    }

    /// <summary>Called by the activity with every result; returns true when it was a scan.</summary>
    public static bool HandleResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != RequestCode)
        {
            return false;
        }

        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(resultCode == Result.Ok ? data?.GetStringExtra("SCAN_RESULT") : null);
        return true;
    }
}
