using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

/// <summary>
/// Calculator window in the look of the iPhone calculator: round keys, orange operators, big right aligned number.
/// Keys, the keyboard and typed words ("5 km to mi") all work.
/// </summary>
internal sealed class CalcWindow : ToolShell
{
    private string _e = "";                 // what was typed, with * and / as operators
    private bool _done;                     // the last thing was "=", the number on screen is an answer
    private string _prev = "";              // the sum that gave that answer
    private readonly TextBlock _small, _big;
    private readonly DispatcherTimer _flash = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private string _note = "";

    public CalcWindow() : base("Calculator", 336)
    {
        _small = Kit.T("", 16, false, Kit.Dim);
        _small.HorizontalAlignment = HorizontalAlignment.Right;
        _small.TextAlignment = TextAlignment.Right;
        _small.Margin = new Thickness(8, 0, 8, 0);
        _small.Height = 24;

        _big = Kit.T("0", 62, false);
        _big.FontWeight = FontWeights.Normal;
        _big.HorizontalAlignment = HorizontalAlignment.Right;
        _big.TextAlignment = TextAlignment.Right;
        _big.TextTrimming = TextTrimming.None;
        _big.Margin = new Thickness(8, 0, 8, 6);
        _big.Height = 78;
        _big.Cursor = Cursors.Hand;
        _big.ToolTip = "Click to copy";
        _big.MouseLeftButtonUp += (_, e) => { e.Handled = true; CopyNow(); };

        var stack = new StackPanel();
        stack.Children.Add(_small);
        stack.Children.Add(_big);

        var grid = new UniformGrid { Columns = 4, Margin = new Thickness(0, 6, 0, 0) };
        // function keys, digits, operators (like the iPhone)
        (string Label, string Kind, Action Do)[] keys =
        {
            ("back", "fn", Back), ("AC", "fn", Clear), ("%", "fn", () => Put("%")), ("÷", "op", () => Op("/")),
            ("7", "d", () => Digit("7")), ("8", "d", () => Digit("8")), ("9", "d", () => Digit("9")), ("×", "op", () => Op("*")),
            ("4", "d", () => Digit("4")), ("5", "d", () => Digit("5")), ("6", "d", () => Digit("6")), ("−", "op", () => Op("-")),
            ("1", "d", () => Digit("1")), ("2", "d", () => Digit("2")), ("3", "d", () => Digit("3")), ("+", "op", () => Op("+")),
            ("±", "fn", Negate), ("0", "d", () => Digit("0")), (".", "d", Dot), ("=", "op", Evaluate),
        };
        foreach (var k in keys) grid.Children.Add(MakeKey(k.Label, k.Kind, k.Do));
        stack.Children.Add(grid);
        Body.Children.Add(stack);

        PreviewTextInput += OnText;
        PreviewKeyDown += OnKeyDown;
        _flash.Tick += (_, _) => { _flash.Stop(); _note = ""; Refresh(); };
    }

    private Border MakeKey(string label, string kind, Action act)
    {
        Color bg, fg;
        if (kind == "op") { bg = Color.FromRgb(0xFF, 0x9F, 0x0A); fg = Colors.White; }
        else if (kind == "fn") { bg = Kit.Dark ? Color.FromRgb(0x63, 0x63, 0x66) : Color.FromRgb(0xD4, 0xD4, 0xD2); fg = Kit.Dark ? Colors.White : Color.FromRgb(0x1D, 0x1D, 0x1F); }
        else { bg = Kit.Dark ? Color.FromRgb(0x33, 0x33, 0x36) : Colors.White; fg = Kit.Dark ? Colors.White : Color.FromRgb(0x1D, 0x1D, 0x1F); }

        UIElement content;
        if (label == "back") content = Icons.Make("backspace", 24, new SolidColorBrush(fg), 1.7);
        else
        {
            var t = new TextBlock
            {
                Text = label, Foreground = new SolidColorBrush(fg), FontSize = kind == "op" ? 34 : 29,
                FontWeight = FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            if (label == "AC") t.FontSize = 26;
            if (label == "%" || label == "±") t.FontSize = 27;
            content = t;
        }
        if (content is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Center; fe.VerticalAlignment = VerticalAlignment.Center; }

        var brush = new SolidColorBrush(bg);
        var b = new Border
        {
            Width = 66, Height = 66, CornerRadius = new CornerRadius(33), Margin = new Thickness(4, 3.5, 4, 3.5), HorizontalAlignment = HorizontalAlignment.Center, Background = brush,
            Cursor = Cursors.Hand, Child = content
        };
        Color light = Color.FromRgb((byte)Math.Min(255, bg.R + 55), (byte)Math.Min(255, bg.G + 55), (byte)Math.Min(255, bg.B + 55));
        b.PreviewMouseLeftButtonDown += (_, _) => brush.Color = light;
        b.PreviewMouseLeftButtonUp += (_, _) =>
        {
            var a = new System.Windows.Media.Animation.ColorAnimation(bg, TimeSpan.FromMilliseconds(260));
            brush.BeginAnimation(SolidColorBrush.ColorProperty, a);
        };
        b.MouseLeave += (_, _) => { if (brush.Color != bg) { brush.BeginAnimation(SolidColorBrush.ColorProperty, null); brush.Color = bg; } };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; act(); };
        return b;
    }

    // ---------- keyboard ----------

    private void OnText(object sender, TextCompositionEventArgs e)
    {
        string t = e.Text;
        if (t.Length != 1) return;
        char c = t[0];
        e.Handled = true;
        if (char.IsDigit(c)) Digit(c.ToString());
        else if (c == '.' || c == ',') Dot();
        else if (c == '+' || c == '-' || c == '*' || c == '/') Op(c.ToString());
        else if (c == '=') Evaluate();
        else if (c == '%') Put("%");
        else if (c == '(' || c == ')' || c == '^' || c == '!') Put(c.ToString());
        else if (char.IsLetter(c) || c == ' ' || c == '°') Raw(c.ToString());
        else e.Handled = false;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; Evaluate(); }
        else if (e.Key == Key.Back) { e.Handled = true; Back(); }
        else if (e.Key == Key.Delete) { e.Handled = true; Clear(); }
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { e.Handled = true; CopyNow(); }
        else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            e.Handled = true;
            try { if (Clipboard.ContainsText()) { string t = Clipboard.GetText().Trim(); if (t.Length <= 60) { if (_done) { _e = ""; _done = false; } _e += t; Refresh(); } } } catch { }
        }
    }

    // ---------- editing ----------

    private static bool IsOp(char c) => c == '+' || c == '-' || c == '*' || c == '/' || c == '^';

    private void StartIfDone(bool keepAnswer)
    {
        if (!_done) return;
        _done = false;
        if (!keepAnswer) _e = "";
        _prev = "";
    }

    private void Raw(string s) { StartIfDone(false); if (_e.Length < 60) _e += s; Refresh(); }

    private void Put(string s) { StartIfDone(true); if (_e.Length < 60) _e += s; Refresh(); }

    private void Digit(string d)
    {
        StartIfDone(false);
        if (_e.Length >= 40) return;
        // "0" then a digit becomes the digit
        if (_e.Length > 0 && _e[^1] == '0' && (_e.Length == 1 || !(char.IsDigit(_e[^2]) || _e[^2] == '.'))) _e = _e.Substring(0, _e.Length - 1);
        _e += d;
        Refresh();
    }

    private void Dot()
    {
        StartIfDone(false);
        int i = _e.Length - 1;
        while (i >= 0 && (char.IsDigit(_e[i]) || _e[i] == '.')) i--;
        string cur = _e.Substring(i + 1);
        if (cur.Contains('.')) return;
        _e += cur.Length == 0 ? "0." : ".";
        Refresh();
    }

    private void Op(string op)
    {
        StartIfDone(true);
        if (_e.Length == 0) { if (op == "-") _e = "-"; else _e = "0" + op; Refresh(); return; }
        if (_e == "-") return;
        char last = _e[^1];
        if (IsOp(last))
        {
            // typing another operator replaces the old one
            _e = _e.Substring(0, _e.Length - 1) + op;
        }
        else _e += op;
        Refresh();
    }

    private void Negate()
    {
        StartIfDone(true);
        if (_e.Length == 0) { _e = "-"; Refresh(); return; }
        int i = _e.Length;
        while (i > 0 && (char.IsDigit(_e[i - 1]) || _e[i - 1] == '.')) i--;
        if (i == _e.Length) return; // no number at the end
        if (i > 0 && _e[i - 1] == '-' && (i - 1 == 0 || IsOp(_e[i - 2]) || _e[i - 2] == '(')) _e = _e.Remove(i - 1, 1);
        else if (i > 0 && _e[i - 1] == '-') _e = _e.Substring(0, i - 1) + "+" + _e.Substring(i);
        else if (i > 0 && _e[i - 1] == '+') _e = _e.Substring(0, i - 1) + "-" + _e.Substring(i);
        else _e = _e.Insert(i, "-");
        Refresh();
    }

    private void Back()
    {
        if (_done) { Clear(); return; }
        if (_e.Length > 0) _e = _e.Substring(0, _e.Length - 1);
        Refresh();
    }

    private void Clear() { _e = ""; _prev = ""; _done = false; _note = ""; Refresh(); }

    private void Evaluate()
    {
        if (_e.Length == 0 || _done) return;
        if (CalcEngine.TryEval(_e, out string disp, out string plain))
        {
            _prev = Show(_e);
            _e = plain.Replace(",", "");
            // an answer with a unit ("3.1 mi") stays as text, a number can be calculated on
            _done = true;
            Refresh();
        }
        else { _note = "Error"; _flash.Stop(); _flash.Start(); Refresh(); }
    }

    private void CopyNow()
    {
        if (_e.Length == 0) return;
        string text = _e;
        if (!_done && CalcEngine.TryEval(_e, out _, out string plain)) text = plain;
        if (Kit.SetClipText(text)) { _note = "Copied"; _flash.Stop(); _flash.Start(); Refresh(); }
    }

    // ---------- screen ----------

    /// <summary>Typed text for the screen: thousands separators in numbers, proper operator signs.</summary>
    private static string Show(string e)
    {
        if (e.Length == 0) return "0";
        bool words = false;
        foreach (char c in e) if (char.IsLetter(c)) { words = true; break; }
        string s = System.Text.RegularExpressions.Regex.Replace(e, @"\d+", m =>
        {
            string d = m.Value;
            if (d.Length <= 3 || words) return d;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < d.Length; i++)
            {
                if (i > 0 && (d.Length - i) % 3 == 0) sb.Append(',');
                sb.Append(d[i]);
            }
            return sb.ToString();
        });
        if (!words) s = s.Replace("*", "×").Replace("/", "÷").Replace("-", "−");
        return s;
    }

    private void Refresh()
    {
        string main = Show(_e);
        _big.Text = _note == "Error" ? "Error" : main;
        int n = _big.Text.Length;
        _big.FontSize = n <= 7 ? 62 : n <= 9 ? 52 : n <= 11 ? 43 : n <= 14 ? 34 : n <= 18 ? 27 : 22;

        string small = "";
        if (_note == "Copied") small = "Copied";
        else if (_done) small = _prev;
        else if (_e.Length > 0 && CalcEngine.TryEval(_e, out string disp, out _) && disp != _e) small = "= " + disp;
        _small.Text = small;
    }
}
