using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LiteIsland;

/// <summary>Type a sum, get the answer. Also percentages ("20% of 150", "100 + 10%") and units ("5 km to mi", "72 f to c").</summary>
internal static class CalcEngine
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // unit -> (category, size in the base unit of that category)
    private static readonly Dictionary<string, (string Cat, double F)> Units = Build();

    private static Dictionary<string, (string, double)> Build()
    {
        var d = new Dictionary<string, (string, double)>();
        void Add(string cat, double f, params string[] names) { foreach (string n in names) d[n] = (cat, f); }
        // length (m)
        Add("len", 1, "m", "meter", "meters", "metre", "metres");
        Add("len", 1000, "km", "kilometer", "kilometers", "kilometre", "kilometres");
        Add("len", 0.01, "cm", "centimeter", "centimeters");
        Add("len", 0.001, "mm", "millimeter", "millimeters");
        Add("len", 1609.344, "mi", "mile", "miles");
        Add("len", 0.3048, "ft", "foot", "feet");
        Add("len", 0.0254, "in", "inch", "inches");
        Add("len", 0.9144, "yd", "yard", "yards");
        // mass (kg)
        Add("mass", 1, "kg", "kilogram", "kilograms", "kilo", "kilos");
        Add("mass", 0.001, "g", "gram", "grams");
        Add("mass", 0.000001, "mg");
        Add("mass", 0.45359237, "lb", "lbs", "pound", "pounds");
        Add("mass", 0.028349523125, "oz", "ounce", "ounces");
        Add("mass", 1000, "ton", "tons", "tonne", "tonnes");
        Add("mass", 6.35029318, "st", "stone");
        // volume (l)
        Add("vol", 1, "l", "liter", "liters", "litre", "litres");
        Add("vol", 0.001, "ml", "milliliter", "milliliters");
        Add("vol", 3.785411784, "gal", "gallon", "gallons");
        Add("vol", 0.946352946, "qt", "quart", "quarts");
        Add("vol", 0.473176473, "pt", "pint", "pints");
        Add("vol", 0.2365882365, "cup", "cups");
        Add("vol", 0.0295735295625, "floz");
        Add("vol", 0.01478676478, "tbsp");
        Add("vol", 0.00492892159, "tsp");
        // speed (m/s)
        Add("spd", 1, "m/s", "mps");
        Add("spd", 1000.0 / 3600, "km/h", "kmh", "kph");
        Add("spd", 0.44704, "mph");
        Add("spd", 0.514444, "kn", "knot", "knots");
        // data (bytes)
        Add("data", 1, "b", "byte", "bytes");
        Add("data", 0.125, "bit", "bits");
        Add("data", 1024, "kb", "kib");
        Add("data", 1024.0 * 1024, "mb", "mib");
        Add("data", 1024.0 * 1024 * 1024, "gb", "gib");
        Add("data", 1024.0 * 1024 * 1024 * 1024, "tb", "tib");
        // time (s)
        Add("time", 0.001, "ms");
        Add("time", 1, "s", "sec", "secs", "second", "seconds");
        Add("time", 60, "min", "mins", "minute", "minutes");
        Add("time", 3600, "h", "hr", "hrs", "hour", "hours");
        Add("time", 86400, "d", "day", "days");
        Add("time", 604800, "wk", "week", "weeks");
        // area (m2)
        Add("area", 1, "m2", "sqm");
        Add("area", 1000000, "km2");
        Add("area", 10000, "ha", "hectare", "hectares");
        Add("area", 4046.8564224, "acre", "acres");
        Add("area", 0.09290304, "ft2", "sqft");
        // angle (degree)
        Add("ang", 1, "deg", "degree", "degrees");
        Add("ang", 180 / Math.PI, "rad", "radian", "radians");
        // temperature is special
        Add("temp", 0, "c", "celsius", "f", "fahrenheit", "k", "kelvin");
        return d;
    }

    private static string Norm(string unit) => unit.Trim().TrimStart('°').ToLowerInvariant();

    private static double ToCelsius(string u, double v) => u is "f" or "fahrenheit" ? (v - 32) * 5 / 9 : u is "k" or "kelvin" ? v - 273.15 : v;
    private static double FromCelsius(string u, double c) => u is "f" or "fahrenheit" ? c * 9 / 5 + 32 : u is "k" or "kelvin" ? c + 273.15 : c;

    /// <summary>display: what is shown (with thousands separators), plain: what is copied.</summary>
    public static bool TryEval(string input, out string display, out string plain)
    {
        display = plain = "";
        string s = (input ?? "").Trim();
        if (s.Length == 0) return false;
        try
        {
            // unit conversion: "5 km to mi"
            Match um = Regex.Match(s, @"^(.+?)\s+(?:to|in|into|as)\s+(\S+)$", RegexOptions.IgnoreCase);
            if (um.Success)
            {
                string left = um.Groups[1].Value.Trim(), target = Norm(um.Groups[2].Value);
                Match lm = Regex.Match(left, @"^(.*?[\d\)\.])\s*([a-zA-Z°µ/²]+)$");
                if (lm.Success && Units.TryGetValue(target, out var tu))
                {
                    string src = Norm(lm.Groups[2].Value);
                    if (Units.TryGetValue(src, out var su) && su.Cat == tu.Cat)
                    {
                        double v = new Parser(lm.Groups[1].Value).Parse();
                        double r = su.Cat == "temp" ? FromCelsius(target, ToCelsius(src, v)) : v * su.F / tu.F;
                        if (double.IsNaN(r) || double.IsInfinity(r)) return false;
                        display = Format(r, true) + " " + UnitLabel(target);
                        plain = Format(r, false) + " " + UnitLabel(target);
                        return true;
                    }
                }
                return false;
            }

            double res = new Parser(s).Parse();
            if (double.IsNaN(res) || double.IsInfinity(res)) return false;
            // a plain number typed in: nothing to show yet
            if (Regex.IsMatch(s, @"^[\d\.,\s]+$")) return false;
            display = Format(res, true);
            plain = Format(res, false);
            return true;
        }
        catch { return false; }
    }

    private static string UnitLabel(string u) => u switch
    {
        "c" or "celsius" => "°C",
        "f" or "fahrenheit" => "°F",
        "k" or "kelvin" => "K",
        _ => u
    };

    public static string Format(double v, bool group)
    {
        if (v == 0) return "0";
        double a = Math.Abs(v);
        if (a >= 1e15 || a < 1e-7) return v.ToString("0.######e+0", Inv);
        // about 10 significant digits, no trailing zeros
        int intDigits = a >= 1 ? (int)Math.Floor(Math.Log10(a)) + 1 : 0;
        int dec = Math.Clamp(10 - intDigits, 0, 10);
        double rounded = Math.Round(v, dec);
        string pattern = (group ? "#,##0" : "0") + (dec > 0 ? "." + new string('#', dec) : "");
        string t = rounded.ToString(pattern, Inv);
        return t == "-0" ? "0" : t;
    }

    // ---------- expression parser ----------

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string text)
        {
            string t = text.Replace("×", "*").Replace("÷", "/").Replace("−", "-").Replace("π", "pi");
            t = Regex.Replace(t, @"(?<=\d),(?=\d{3}(\D|$))", ""); // 1,000 -> 1000
            t = Regex.Replace(t, @"(?i)([\d\.]+)\s*%\s*of\s+(.+)", "($1/100)*($2)");
            _s = t.ToLowerInvariant();
        }

        public double Parse()
        {
            double v = Add(out _);
            Ws();
            if (_i < _s.Length) throw new FormatException();
            return v;
        }

        private void Ws() { while (_i < _s.Length && _s[_i] == ' ') _i++; }
        private bool At(char c) { Ws(); return _i < _s.Length && _s[_i] == c; }

        private double Add(out bool pct)
        {
            double v = Mul(out pct);
            while (true)
            {
                if (At('+')) { _i++; double r = Mul(out bool rp); v = rp ? v + v * r : v + r; pct = false; }
                else if (At('-')) { _i++; double r = Mul(out bool rp); v = rp ? v - v * r : v - r; pct = false; }
                else break;
            }
            return v;
        }

        private double Mul(out bool pct)
        {
            double v = Unary(out pct);
            while (true)
            {
                Ws();
                if (_i >= _s.Length) break;
                char c = _s[_i];
                if (c == '*') { _i++; if (_i < _s.Length && _s[_i] == '*') { _i++; v = Math.Pow(v, Unary(out _)); } else v *= Unary(out _); pct = false; }
                else if (c == '/') { _i++; double r = Unary(out _); if (r == 0) throw new DivideByZeroException(); v /= r; pct = false; }
                else if (c == 'x' && TimesX()) { _i++; v *= Unary(out _); pct = false; }
                else if (string.CompareOrdinal(_s, _i, "mod", 0, 3) == 0) { _i += 3; double r = Unary(out _); v %= r; pct = false; }
                else if (c == '(' || c == '√' || (char.IsLetter(c) && c != 'x') || (c == 'x' && !TimesX() && _i + 1 < _s.Length && char.IsLetter(_s[_i + 1])))
                {
                    v *= Unary(out _); pct = false; // 2(3+4), 2pi, 3 sqrt(4)
                }
                else break;
            }
            return v;
        }

        // "5 x 3": x is a times sign when a number or bracket follows
        private bool TimesX()
        {
            int j = _i + 1;
            while (j < _s.Length && _s[j] == ' ') j++;
            return j < _s.Length && (char.IsDigit(_s[j]) || _s[j] == '(' || _s[j] == '.');
        }

        private double Unary(out bool pct)
        {
            pct = false;
            if (At('-')) { _i++; return -Unary(out pct); }
            if (At('+')) { _i++; return Unary(out pct); }
            return Power(out pct);
        }

        private double Power(out bool pct)
        {
            double b = Postfix(out pct);
            if (At('^')) { _i++; double e = Unary(out _); pct = false; return Math.Pow(b, e); }
            return b;
        }

        private double Postfix(out bool pct)
        {
            double v = Primary();
            pct = false;
            while (true)
            {
                if (At('%')) { _i++; v /= 100; pct = true; }
                else if (At('!')) { _i++; v = Fact(v); pct = false; }
                else break;
            }
            return v;
        }

        private static double Fact(double n)
        {
            if (n < 0 || n > 170 || Math.Abs(n - Math.Round(n)) > 1e-9) throw new FormatException();
            double r = 1;
            for (int k = 2; k <= (int)Math.Round(n); k++) r *= k;
            return r;
        }

        private double Primary()
        {
            Ws();
            if (_i >= _s.Length) throw new FormatException();
            char c = _s[_i];
            if (c == '(')
            {
                _i++;
                double v = Add(out _);
                if (!At(')')) throw new FormatException();
                _i++;
                return v;
            }
            if (c == '√') { _i++; return Math.Sqrt(Unary(out _)); }
            if (char.IsDigit(c) || c == '.')
            {
                int st = _i;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                if (_i < _s.Length && _s[_i] == 'e' && _i + 1 < _s.Length && (char.IsDigit(_s[_i + 1]) || ((_s[_i + 1] == '-' || _s[_i + 1] == '+') && _i + 2 < _s.Length && char.IsDigit(_s[_i + 2]))))
                {
                    _i += 2;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }
                return double.Parse(_s.Substring(st, _i - st), NumberStyles.Float, Inv);
            }
            if (char.IsLetter(c))
            {
                int st = _i;
                while (_i < _s.Length && (char.IsLetter(_s[_i]) || char.IsDigit(_s[_i]) && _i > st && !char.IsDigit(_s[st]))) _i++;
                string name = _s.Substring(st, _i - st);
                if (name == "pi") return Math.PI;
                if (name == "e") return Math.E;
                double arg;
                if (At('(')) { _i++; arg = Add(out _); if (!At(')')) throw new FormatException(); _i++; }
                else arg = Unary(out _);
                const double R = Math.PI / 180;
                switch (name)
                {
                    case "sqrt": return Math.Sqrt(arg);
                    case "cbrt": return Math.Cbrt(arg);
                    case "sin": return Math.Round(Math.Sin(arg * R), 12);
                    case "cos": return Math.Round(Math.Cos(arg * R), 12);
                    case "tan": return Math.Round(Math.Tan(arg * R), 12);
                    case "asin": return Math.Asin(arg) / R;
                    case "acos": return Math.Acos(arg) / R;
                    case "atan": return Math.Atan(arg) / R;
                    case "ln": return Math.Log(arg);
                    case "log": return Math.Log10(arg);
                    case "log2": return Math.Log2(arg);
                    case "exp": return Math.Exp(arg);
                    case "abs": return Math.Abs(arg);
                    case "round": return Math.Round(arg, MidpointRounding.AwayFromZero);
                    case "floor": return Math.Floor(arg);
                    case "ceil": return Math.Ceiling(arg);
                    case "fact": return Fact(arg);
                    default: throw new FormatException();
                }
            }
            throw new FormatException();
        }
    }
}

/// <summary>Calculator window: type, see the answer at once, Enter copies it.</summary>
internal sealed class CalcWindow : ToolShell
{
    private readonly TextBox _in = new();
    private readonly TextBlock _hint, _out, _sub;
    private readonly StackPanel _hist = new();
    private readonly List<(string Expr, string Res)> _h = new();
    private string _plain = "";
    private int _recall = -1;
    private readonly DispatcherTimer _flash = new() { Interval = TimeSpan.FromMilliseconds(1100) };

    public CalcWindow() : base("Calculator", 400)
    {
        _in.Background = Brushes.Transparent;
        _in.BorderThickness = new Thickness(0);
        _in.Foreground = Kit.Text;
        _in.CaretBrush = Kit.Accent;
        _in.FontSize = 26;
        _in.FontWeight = FontWeights.Medium;
        _in.Padding = new Thickness(0);
        _in.VerticalContentAlignment = VerticalAlignment.Center;
        _in.SelectionBrush = Kit.Accent;
        _in.AutoWordSelection = false;
        _in.TextChanged += (_, _) => Recalc();
        _in.PreviewKeyDown += OnInputKey;

        _hint = Kit.T("Type a sum or a conversion", 26, false, Kit.Faint);
        _hint.FontWeight = FontWeights.Medium;
        _hint.IsHitTestVisible = false;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.TextTrimming = TextTrimming.None;
        _hint.FontSize = 21;

        var field = new Grid { Height = 54 };
        field.Children.Add(_hint);
        field.Children.Add(_in);
        var fieldBox = new Border { Background = Kit.Fill, CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 0, 18, 0), Child = field };

        _out = Kit.T("", 40, true, Kit.Accent);
        _out.FontWeight = FontWeights.SemiBold;
        _out.Margin = new Thickness(6, 14, 6, 0);
        _out.TextAlignment = TextAlignment.Right;
        _out.Height = 56;
        _out.Opacity = 0.0;

        _sub = Kit.T("Enter copies the answer", 12, false, Kit.Dim);
        _sub.HorizontalAlignment = HorizontalAlignment.Right;
        _sub.Margin = new Thickness(6, 0, 6, 0);

        var stack = new StackPanel();
        stack.Children.Add(fieldBox);
        stack.Children.Add(_out);
        stack.Children.Add(_sub);

        var tips = Kit.T("12 × 8   ·   20% of 150   ·   5 km to mi   ·   sqrt(81)", 11.5, false, Kit.Faint);
        tips.HorizontalAlignment = HorizontalAlignment.Center;
        tips.Margin = new Thickness(0, 16, 0, 0);
        tips.Name = "Tips";
        stack.Children.Add(tips);

        _hist.Margin = new Thickness(0, 12, 0, 0);
        stack.Children.Add(_hist);
        Body.Children.Add(stack);
        _flash.Tick += (_, _) => { _flash.Stop(); _sub.Text = "Enter copies the answer"; };
    }

    protected override void OnShown() => _in.Focus();

    private void Recalc()
    {
        _hint.Visibility = _in.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _recall = -1;
        bool ok = CalcEngine.TryEval(_in.Text, out string disp, out string plain);
        _plain = ok ? plain : "";
        _out.Text = ok ? disp : "";
        double to = ok ? 1 : 0;
        _out.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ok ? 140 : 90)));
        // long answers shrink so they always fit
        _out.FontSize = disp.Length > 16 ? 26 : disp.Length > 11 ? 32 : 40;
        _flash.Stop();
        _sub.Text = ok ? "Enter copies the answer" : (_in.Text.Trim().Length == 0 ? "Enter copies the answer" : "…");
    }

    private void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (_plain.Length == 0) return;
            Kit.SetClipText(_plain);
            _h.Insert(0, (_in.Text.Trim(), _out.Text));
            if (_h.Count > 5) _h.RemoveAt(_h.Count - 1);
            _sub.Text = "Copied";
            _flash.Stop(); _flash.Start();
            RenderHistory();
            _in.Clear();
            _out.Text = "";
        }
        else if (e.Key == Key.Up && _h.Count > 0)
        {
            e.Handled = true;
            _recall = Math.Min(_h.Count - 1, _recall + 1);
            int r = _recall;
            _in.Text = _h[r].Expr;
            _recall = r; // the text change reset it
            _in.CaretIndex = _in.Text.Length;
        }
        else if (e.Key == Key.Down && _recall >= 0)
        {
            e.Handled = true;
            int r = _recall - 1;
            if (r < 0) { _in.Clear(); return; }
            _in.Text = _h[r].Expr;
            _recall = r;
            _in.CaretIndex = _in.Text.Length;
        }
    }

    private void RenderHistory()
    {
        _hist.Children.Clear();
        foreach (var item in _h)
        {
            var row = new Grid { Height = 36 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var e = Kit.T(item.Expr, 13.5, false, Kit.Dim);
            e.VerticalAlignment = VerticalAlignment.Center;
            e.Margin = new Thickness(14, 0, 10, 0);
            var r = Kit.T(item.Res, 15, true);
            r.VerticalAlignment = VerticalAlignment.Center;
            r.Margin = new Thickness(0, 0, 14, 0);
            Grid.SetColumn(r, 1);
            row.Children.Add(e);
            row.Children.Add(r);
            var card = new Border { Background = Brushes.Transparent, CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, Child = row, Margin = new Thickness(0, 1, 0, 1) };
            string expr = item.Expr;
            card.MouseEnter += (_, _) => card.Background = Kit.Fill;
            card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;
            card.MouseLeftButtonUp += (_, ev) => { ev.Handled = true; _in.Text = expr; _in.CaretIndex = expr.Length; _in.Focus(); };
            _hist.Children.Add(card);
        }
        if (_hist.Children.Count > 0) Kit.FadeIn(_hist, 200, 0.98);
    }
}
