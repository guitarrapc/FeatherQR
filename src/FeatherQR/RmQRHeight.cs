namespace FeatherQR;

/// <summary>
/// Fixed rMQR symbol height in modules for automatic width selection ("fixed height, automatic width"): the generator only considers the versions of this height and picks among them by <see cref="RmQRFitStrategy"/>.
/// Values are the module heights themselves.
/// </summary>
public enum RmQRHeight
{
    /// <summary>7 modules high (widths 43-139).</summary>
    H7 = 7,
    /// <summary>9 modules high (widths 43-139).</summary>
    H9 = 9,
    /// <summary>11 modules high (widths 27-139).</summary>
    H11 = 11,
    /// <summary>13 modules high (widths 27-139).</summary>
    H13 = 13,
    /// <summary>15 modules high (widths 43-139).</summary>
    H15 = 15,
    /// <summary>17 modules high (widths 43-139).</summary>
    H17 = 17,
}
