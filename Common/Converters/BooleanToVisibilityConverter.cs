using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Common.Converters
{
    /// <summary>
    /// Converts Boolean values to Visibility values.
    /// </summary>
    [ValueConversion(typeof(bool), typeof(Visibility))]
    public class BooleanToVisibilityConverter : IValueConverter
    {
        /// <summary>
        /// Gets or sets the Visibility value to return when the input is true.
        /// Defaults to Visibility.Visible.
        /// </summary>
        public Visibility TrueValue { get; set; } = Visibility.Visible;

        /// <summary>
        /// Gets or sets the Visibility value to return when the input is false.
        /// Defaults to Visibility.Collapsed.
        /// </summary>
        public Visibility FalseValue { get; set; } = Visibility.Collapsed;

        /// <summary>
        /// Converts a boolean value to a Visibility value.
        /// </summary>
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool boolValue) return boolValue ? TrueValue : FalseValue;
            // Attempt to handle nullable bool
            if (value is bool b)
            {
                bool? nullableBool = b;
                boolValue = nullableBool.Value;
            }
            else
            {
                boolValue = false;
            }

            return boolValue ? TrueValue : FalseValue;
        }

        /// <summary>
        /// Converts a Visibility value back to a boolean value.
        /// </summary>
        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is Visibility visibilityValue)
            {
                return visibilityValue == TrueValue;
            }
            return false; // Default fallback
        }
    }
} 