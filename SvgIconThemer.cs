using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace TopBarAdaptiveColor
{
    internal sealed class SvgIconThemer
    {
        private const string DarkInkHex = "#1F1F1F";
        private const string LightInkHex = "#FFFFFF";

        private static readonly string CacheRoot = Path.Combine(
            Path.GetTempPath(), "MagicTaskbar", "adaptive-icons");

        private static readonly Regex MonochromeAttrRegex = new(
            @"(?<prefix>(?:fill|stroke|stop-color)\s*=\s*[""'])\s*(?:#(?:fff(?:fff)?|000(?:000)?)|white|black|currentColor|rgb\(\s*255\s*,\s*255\s*,\s*255\s*\)|rgb\(\s*0\s*,\s*0\s*,\s*0\s*\))\s*(?<suffix>[""'])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MonochromeCssRegex = new(
            @"(?<prefix>(?:fill|stroke|stop-color)\s*:\s*)(?:#(?:fff(?:fff)?|000(?:000)?)|white|black|currentColor|rgb\(\s*255\s*,\s*255\s*,\s*255\s*\)|rgb\(\s*0\s*,\s*0\s*,\s*0\s*\))(?<suffix>\s*[;""'])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly ConcurrentDictionary<string, Uri?> CachedDarkUris = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object CustomTintSync = new();
        private static readonly ConditionalWeakTable<DependencyObject, string> CustomAlertHex = new();
        private static readonly List<WeakReference<SvgIconThemer>> ActiveInstances = new();
        private static SvgIconThemer? _fallbackAlertThemer;

        private readonly Type? _svgViewboxType;
        private readonly DependencyProperty? _sourceProperty;
        private readonly DependencyPropertyDescriptor? _sourceDescriptor;
        private readonly ConditionalWeakTable<DependencyObject, Uri> _originalUris = new();
        private readonly ConditionalWeakTable<DependencyObject, object> _trackedHandlers = new();

        private bool _isDarkMode;
        private bool _isUpdatingSource;

        public SvgIconThemer()
        {
            _svgViewboxType = Type.GetType("SharpVectors.Converters.SvgViewbox, SharpVectors.Converters.Wpf");
            if (_svgViewboxType != null)
            {
                var field = _svgViewboxType.GetField("SourceProperty", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                _sourceProperty = field?.GetValue(null) as DependencyProperty;
                if (_sourceProperty != null)
                {
                    _sourceDescriptor = DependencyPropertyDescriptor.FromProperty(_sourceProperty, _svgViewboxType);
                }
            }

            lock (CustomTintSync)
            {
                ActiveInstances.Add(new WeakReference<SvgIconThemer>(this));
            }
        }

        internal static void SetElementAlertHex(DependencyObject? svgElement, string? hex)
        {
            if (svgElement == null)
            {
                return;
            }

            List<SvgIconThemer> live = new();
            lock (CustomTintSync)
            {
                CustomAlertHex.Remove(svgElement);
                if (!string.IsNullOrEmpty(hex))
                {
                    CustomAlertHex.Add(svgElement, hex);
                }

                for (int i = ActiveInstances.Count - 1; i >= 0; i--)
                {
                    if (ActiveInstances[i].TryGetTarget(out var inst))
                    {
                        live.Add(inst);
                    }
                    else
                    {
                        ActiveInstances.RemoveAt(i);
                    }
                }
            }

            bool applied = false;
            foreach (var inst in live)
            {
                if (inst.IsSvgViewbox(svgElement))
                {
                    inst.ApplyToSvg(svgElement, inst._isDarkMode);
                    applied = true;
                }
            }

            if (!applied)
            {
                _fallbackAlertThemer ??= new SvgIconThemer();
                if (_fallbackAlertThemer.IsSvgViewbox(svgElement))
                {
                    _fallbackAlertThemer.ApplyToSvg(svgElement, false);
                }
            }
        }

        public bool IsAvailable => _svgViewboxType != null && _sourceProperty != null;

        public bool IsSvgViewbox(DependencyObject obj) =>
            _svgViewboxType != null && _svgViewboxType.IsInstanceOfType(obj);

        public void SetDarkForeground(bool useDarkForeground)
        {
            _isDarkMode = useDarkForeground;
        }

        public void ApplyToSvg(DependencyObject svgElement, bool useDarkForeground)
        {
            if (_sourceProperty == null || !IsSvgViewbox(svgElement))
            {
                return;
            }

            EnsureTracked(svgElement);

            var currentUri = svgElement.GetValue(_sourceProperty) as Uri;
            if (currentUri == null)
            {
                return;
            }

            if (!IsOurGeneratedUri(currentUri))
            {
                _originalUris.Remove(svgElement);
                _originalUris.Add(svgElement, currentUri);
            }

            string? customHex = null;
            lock (CustomTintSync)
            {
                CustomAlertHex.TryGetValue(svgElement, out customHex);
            }

            if (!useDarkForeground && string.IsNullOrEmpty(customHex))
            {
                if (IsOurGeneratedUri(currentUri) && _originalUris.TryGetValue(svgElement, out var origUri))
                {
                    SetSourceGuarded(svgElement, origUri);
                }
                return;
            }

            if (!_originalUris.TryGetValue(svgElement, out var baseUri))
            {
                baseUri = currentUri;
                _originalUris.Add(svgElement, baseUri);
            }

            string targetHex = !string.IsNullOrEmpty(customHex) ? customHex! : DarkInkHex;
            Uri? themedUri = GetOrCreateThemedUri(baseUri, targetHex);
            if (themedUri != null && !Equals(currentUri, themedUri))
            {
                SetSourceGuarded(svgElement, themedUri);
            }
        }

        public void DetachAll(DependencyObject root)
        {
            // Restore any modified SVGs on detach/cleanup if needed.
            SetDarkForeground(false);
        }

        private void EnsureTracked(DependencyObject svgElement)
        {
            if (_sourceDescriptor == null || _trackedHandlers.TryGetValue(svgElement, out _))
            {
                return;
            }

            EventHandler handler = (_, _) => OnSvgSourceChanged(svgElement);
            _sourceDescriptor.AddValueChanged(svgElement, handler);
            _trackedHandlers.Add(svgElement, handler);
        }

        private void OnSvgSourceChanged(DependencyObject svgElement)
        {
            if (_isUpdatingSource || _sourceProperty == null)
            {
                return;
            }

            var currentUri = svgElement.GetValue(_sourceProperty) as Uri;
            if (currentUri == null)
            {
                return;
            }

            if (!IsOurGeneratedUri(currentUri))
            {
                _originalUris.Remove(svgElement);
                _originalUris.Add(svgElement, currentUri);

                string? customHex = null;
                lock (CustomTintSync)
                {
                    CustomAlertHex.TryGetValue(svgElement, out customHex);
                }

                string? targetHex = !string.IsNullOrEmpty(customHex)
                    ? customHex
                    : (_isDarkMode ? DarkInkHex : null);
                if (!string.IsNullOrEmpty(targetHex))
                {
                    Uri? themedUri = GetOrCreateThemedUri(currentUri, targetHex!);
                    if (themedUri != null && !Equals(currentUri, themedUri))
                    {
                        SetSourceGuarded(svgElement, themedUri);
                    }
                }
            }
        }

        private void SetSourceGuarded(DependencyObject svgElement, Uri targetUri)
        {
            if (_sourceProperty == null)
            {
                return;
            }

            bool prev = _isUpdatingSource;
            _isUpdatingSource = true;
            try
            {
                svgElement.SetValue(_sourceProperty, targetUri);
            }
            catch
            {
                // Ignore transient SharpVectors URI load errors.
            }
            finally
            {
                _isUpdatingSource = prev;
            }
        }

        private static bool IsOurGeneratedUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri || !uri.IsFile)
            {
                return false;
            }

            return uri.LocalPath.StartsWith(CacheRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static Uri? GetOrCreateThemedUri(Uri sourceUri, string targetHex)
        {
            string cacheKey = $"{targetHex}|{sourceUri}";
            return CachedDarkUris.GetOrAdd(cacheKey, _ => BuildThemedSvgFile(sourceUri, targetHex));
        }

        private static Uri? BuildThemedSvgFile(Uri sourceUri, string targetHex)
        {
            try
            {
                string? svgText = TryReadSvgText(sourceUri);
                if (string.IsNullOrEmpty(svgText))
                {
                    return null;
                }

                // Skip purely raster-pattern SVGs with no monochrome vector strokes/fills.
                bool hasMonochromeAttr = MonochromeAttrRegex.IsMatch(svgText);
                bool hasMonochromeCss = MonochromeCssRegex.IsMatch(svgText);
                if (!hasMonochromeAttr && !hasMonochromeCss)
                {
                    return null;
                }

                string replaced = MonochromeAttrRegex.Replace(svgText, m => $"{m.Groups["prefix"].Value}{targetHex}{m.Groups["suffix"].Value}");
                replaced = MonochromeCssRegex.Replace(replaced, m => $"{m.Groups["prefix"].Value}{targetHex}{m.Groups["suffix"].Value}");

                if (string.Equals(replaced, svgText, StringComparison.Ordinal))
                {
                    return null;
                }

                Directory.CreateDirectory(CacheRoot);
                byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{targetHex}|{sourceUri}"));
                string fileName = Convert.ToHexString(hashBytes, 0, 12).ToLowerInvariant() + ".svg";
                string outPath = Path.Combine(CacheRoot, fileName);
                File.WriteAllText(outPath, replaced, new UTF8Encoding(false));
                return new Uri(outPath, UriKind.Absolute);
            }
            catch
            {
                return null;
            }
        }

        private static string? TryReadSvgText(Uri sourceUri)
        {
            try
            {
                if (sourceUri.IsAbsoluteUri && sourceUri.IsFile)
                {
                    string localPath = sourceUri.LocalPath;
                    if (File.Exists(localPath))
                    {
                        return File.ReadAllText(localPath, Encoding.UTF8);
                    }
                }

                string raw = sourceUri.ToString();
                string relPath = raw;

                const string packPrefix = "pack://application:,,,";
                if (relPath.StartsWith(packPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    relPath = relPath.Substring(packPrefix.Length);
                }

                int compIdx = relPath.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
                if (compIdx >= 0)
                {
                    relPath = relPath.Substring(compIdx + ";component/".Length);
                }

                relPath = relPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                string diskCandidate = Path.Combine(AppContext.BaseDirectory, relPath);
                if (File.Exists(diskCandidate))
                {
                    return File.ReadAllText(diskCandidate, Encoding.UTF8);
                }

                Uri packUri = sourceUri.IsAbsoluteUri
                    ? sourceUri
                    : new Uri(packPrefix + (raw.StartsWith('/') ? raw : "/" + raw), UriKind.Absolute);

                var resStream = Application.GetResourceStream(packUri);
                if (resStream != null)
                {
                    using var sr = new StreamReader(resStream.Stream, Encoding.UTF8);
                    return sr.ReadToEnd();
                }
            }
            catch
            {
                // Best-effort SVG read.
            }

            return null;
        }
    }
}
