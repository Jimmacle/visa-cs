using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VisaCs.Discovery;

/// <summary>
/// A VISA find expression (viFindRsrc): a VISA regular expression optionally followed by an attribute
/// expression in braces, e.g. <c>USB?*INSTR{VI_ATTR_MANF_ID==0x0957 &amp;&amp; VI_ATTR_MODEL_CODE!=0x1796}</c>.
/// </summary>
public sealed class VisaPattern
{
    private readonly Regex _regex;
    private readonly AttributeExpression? _attributes;

    private VisaPattern(string expression, Regex regex, AttributeExpression? attributes)
    {
        Expression = expression;
        _regex = regex;
        _attributes = attributes;
    }

    /// <summary>The original find expression.</summary>
    public string Expression { get; }

    /// <summary>True if the expression has an attribute filter in braces.</summary>
    public bool HasAttributeExpression => _attributes is not null;

    /// <summary>Parses a find expression. Throws <see cref="VisaException"/> (VI_ERROR_INV_EXPR) if invalid.</summary>
    public static VisaPattern Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        int brace = FindAttributeStart(expression);
        string regexPart = brace < 0 ? expression : expression[..brace];
        AttributeExpression? attributes = null;
        if (brace >= 0)
        {
            string rest = expression[brace..].Trim();
            if (!rest.EndsWith('}'))
                throw new VisaException(VisaStatus.ErrorInvalidExpression, "Attribute expression must end with '}'.");
            attributes = AttributeExpression.Parse(rest[1..^1]);
        }
        Regex regex;
        try
        {
            regex = new Regex(ToDotNetRegex(regexPart), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException ex)
        {
            throw new VisaException(VisaStatus.ErrorInvalidExpression, $"Invalid expression '{expression}': {ex.Message}", ex);
        }
        return new VisaPattern(expression, regex, attributes);
    }

    private static int FindAttributeStart(string expression)
    {
        bool inBracket = false;
        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            if (c == '\\') { i++; continue; }
            if (c == '[') inBracket = true;
            else if (c == ']') inBracket = false;
            else if (c == '{' && !inBracket) return i;
        }
        return -1;
    }

    /// <summary>Converts the VISA regular expression syntax to a .NET regular expression.</summary>
    internal static string ToDotNetRegex(string visa)
    {
        var sb = new StringBuilder("^(?:");
        for (int i = 0; i < visa.Length; i++)
        {
            char c = visa[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < visa.Length)
                    {
                        sb.Append(Regex.Escape(visa[i + 1].ToString()));
                        i++;
                    }
                    else
                    {
                        sb.Append(@"\\");
                    }
                    break;
                case '?':
                    sb.Append('.');
                    break;
                case '*':
                case '+':
                case '|':
                case '(':
                case ')':
                    sb.Append(c);
                    break;
                case '[':
                {
                    int j = i + 1;
                    sb.Append('[');
                    if (j < visa.Length && visa[j] == '^')
                    {
                        sb.Append('^');
                        j++;
                    }
                    bool closed = false;
                    for (; j < visa.Length; j++)
                    {
                        char d = visa[j];
                        if (d == ']') { closed = true; break; }
                        if (d == '\\' && j + 1 < visa.Length)
                        {
                            sb.Append('\\').Append(visa[j + 1]);
                            j++;
                            continue;
                        }
                        if (d is '[' or '\\' or '^') sb.Append('\\');
                        sb.Append(d);
                    }
                    if (!closed) throw new VisaException(VisaStatus.ErrorInvalidExpression, "Unterminated '[' in expression.");
                    sb.Append(']');
                    i = j;
                    break;
                }
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append(")$");
        return sb.ToString();
    }

    /// <summary>Tests a resource name and, if the pattern has an attribute expression, its attributes.</summary>
    public bool IsMatch(string resourceName, IReadOnlyDictionary<VisaAttribute, object>? attributes = null)
    {
        if (!_regex.IsMatch(resourceName)) return false;
        if (_attributes is null) return true;
        return _attributes.Evaluate(attributes ?? new Dictionary<VisaAttribute, object>());
    }

    /// <summary>Recursive-descent parser/evaluator for VISA attribute expressions.</summary>
    private abstract class AttributeExpression
    {
        public abstract bool Evaluate(IReadOnlyDictionary<VisaAttribute, object> attributes);

        public static AttributeExpression Parse(string text)
        {
            var tokens = Tokenize(text);
            int pos = 0;
            var expr = ParseOr(tokens, ref pos);
            if (pos != tokens.Count)
                throw new VisaException(VisaStatus.ErrorInvalidExpression, $"Unexpected token '{tokens[pos].Text}' in attribute expression.");
            return expr;
        }

        private enum TokenKind { Identifier, Number, String, Operator, LParen, RParen }

        private readonly record struct Token(TokenKind Kind, string Text);

        private static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '(') { tokens.Add(new(TokenKind.LParen, "(")); i++; continue; }
                if (c == ')') { tokens.Add(new(TokenKind.RParen, ")")); i++; continue; }
                if (c == '"')
                {
                    int end = s.IndexOf('"', i + 1);
                    if (end < 0) throw new VisaException(VisaStatus.ErrorInvalidExpression, "Unterminated string literal.");
                    tokens.Add(new(TokenKind.String, s[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                    tokens.Add(new(TokenKind.Identifier, s[start..i]));
                    continue;
                }
                if (char.IsDigit(c) || (c == '-' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    int start = i;
                    i++;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]))) i++;
                    tokens.Add(new(TokenKind.Number, s[start..i]));
                    continue;
                }
                string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||")
                {
                    tokens.Add(new(TokenKind.Operator, two));
                    i += 2;
                    continue;
                }
                if (c is '<' or '>' or '!')
                {
                    tokens.Add(new(TokenKind.Operator, c.ToString()));
                    i++;
                    continue;
                }
                throw new VisaException(VisaStatus.ErrorInvalidExpression, $"Unexpected character '{c}' in attribute expression.");
            }
            return tokens;
        }

        private static AttributeExpression ParseOr(List<Token> t, ref int pos)
        {
            var left = ParseAnd(t, ref pos);
            while (pos < t.Count && t[pos] is { Kind: TokenKind.Operator, Text: "||" })
            {
                pos++;
                left = new Binary(left, ParseAnd(t, ref pos), isAnd: false);
            }
            return left;
        }

        private static AttributeExpression ParseAnd(List<Token> t, ref int pos)
        {
            var left = ParseUnary(t, ref pos);
            while (pos < t.Count && t[pos] is { Kind: TokenKind.Operator, Text: "&&" })
            {
                pos++;
                left = new Binary(left, ParseUnary(t, ref pos), isAnd: true);
            }
            return left;
        }

        private static AttributeExpression ParseUnary(List<Token> t, ref int pos)
        {
            if (pos >= t.Count) throw new VisaException(VisaStatus.ErrorInvalidExpression, "Incomplete attribute expression.");
            if (t[pos] is { Kind: TokenKind.Operator, Text: "!" })
            {
                pos++;
                return new Not(ParseUnary(t, ref pos));
            }
            if (t[pos].Kind == TokenKind.LParen)
            {
                pos++;
                var inner = ParseOr(t, ref pos);
                if (pos >= t.Count || t[pos].Kind != TokenKind.RParen)
                    throw new VisaException(VisaStatus.ErrorInvalidExpression, "Missing ')' in attribute expression.");
                pos++;
                return inner;
            }
            if (t[pos].Kind != TokenKind.Identifier)
                throw new VisaException(VisaStatus.ErrorInvalidExpression, $"Expected an attribute name, got '{t[pos].Text}'.");
            string name = t[pos++].Text;
            if (!VisaAttributeNames.TryParse(name, out var attribute))
                throw new VisaException(VisaStatus.ErrorInvalidExpression, $"Unknown attribute '{name}'.");
            if (pos >= t.Count || t[pos].Kind != TokenKind.Operator || t[pos].Text is "&&" or "||" or "!")
                throw new VisaException(VisaStatus.ErrorInvalidExpression, $"Expected a comparison after '{name}'.");
            string op = t[pos++].Text;
            if (pos >= t.Count) throw new VisaException(VisaStatus.ErrorInvalidExpression, "Missing value in comparison.");
            var valueToken = t[pos++];
            object value = valueToken.Kind switch
            {
                TokenKind.Number => ParseNumber(valueToken.Text),
                TokenKind.String => valueToken.Text,
                TokenKind.Identifier => valueToken.Text.ToUpperInvariant() switch
                {
                    "VI_TRUE" => 1L,
                    "VI_FALSE" => 0L,
                    _ => valueToken.Text,
                },
                _ => throw new VisaException(VisaStatus.ErrorInvalidExpression, $"Invalid value '{valueToken.Text}'."),
            };
            return new Comparison(attribute, op, value);
        }

        private static long ParseNumber(string text)
        {
            bool negative = text.StartsWith('-');
            string digits = negative ? text[1..] : text;
            long value;
            bool ok = digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? long.TryParse(digits.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
                : long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            if (!ok) throw new VisaException(VisaStatus.ErrorInvalidExpression, $"Invalid number '{text}'.");
            return negative ? -value : value;
        }

        private sealed class Binary(AttributeExpression left, AttributeExpression right, bool isAnd) : AttributeExpression
        {
            public override bool Evaluate(IReadOnlyDictionary<VisaAttribute, object> a) =>
                isAnd ? left.Evaluate(a) && right.Evaluate(a) : left.Evaluate(a) || right.Evaluate(a);
        }

        private sealed class Not(AttributeExpression inner) : AttributeExpression
        {
            public override bool Evaluate(IReadOnlyDictionary<VisaAttribute, object> a) => !inner.Evaluate(a);
        }

        private sealed class Comparison(VisaAttribute attribute, string op, object value) : AttributeExpression
        {
            public override bool Evaluate(IReadOnlyDictionary<VisaAttribute, object> attributes)
            {
                if (!attributes.TryGetValue(attribute, out var actual) || actual is null) return false;
                int cmp;
                if (value is string s)
                {
                    cmp = string.Compare(Convert.ToString(actual, CultureInfo.InvariantCulture), s, StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    long expected = (long)value;
                    long actualNumber;
                    try
                    {
                        actualNumber = actual switch
                        {
                            bool b => b ? 1 : 0,
                            Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture),
                            string str when long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                            _ => Convert.ToInt64(actual, CultureInfo.InvariantCulture),
                        };
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                    cmp = actualNumber.CompareTo(expected);
                }
                return op switch
                {
                    "==" => cmp == 0,
                    "!=" => cmp != 0,
                    "<" => cmp < 0,
                    ">" => cmp > 0,
                    "<=" => cmp <= 0,
                    ">=" => cmp >= 0,
                    _ => false,
                };
            }
        }
    }
}
