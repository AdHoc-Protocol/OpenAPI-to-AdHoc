using System.Collections.Generic;

namespace org.unirail.adhoc;

/// <summary>
/// The naming rules AdHocAgent applies to every entity of a protocol description, kept as a local copy so the
/// converter needs nothing from the agent: a name that is a keyword in any target language (C#, C++, Java,
/// TypeScript, Rust, Go) gets its first lowercase letter capitalised (then the next one, ...) until it is free.
/// </summary>
public static class AdHocNames{
    /// <summary>
    /// Brushes a name the way the agent does. <paramref name="class_name"/> is the name of the enclosing entity:
    /// a pack cannot contain a field or a nested type with its own name, so that one is renamed as well.
    /// </summary>
    public static string brush(string name, string class_name = "")
    {
        if( name != class_name && (name.Equals("_DefaultMaxLengthOf") || !is_prohibited(name)) ) return name;

        var new_name = name;

        for( var i = 0; i < name.Length; i++ )
            if( char.IsLower(name[i]) )
            {
                new_name = new_name[..i] + char.ToUpper(new_name[i]) + new_name[(i + 1)..];
                if( new_name == class_name || is_prohibited(new_name) ) continue;

                return new_name;
            }

        return name;
    }

    /// <summary>True when the name is a keyword of C#, C++, Java, TypeScript, Rust or Go.</summary>
    public static bool is_prohibited(string name) => PROHIBITED.Contains(name);

    static readonly HashSet<string> PROHIBITED =
    [
        // C#
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
        "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
        "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
        "using", "virtual", "void", "volatile",
        // C++
        "alignas", "alignof", "and", "and_eq", "asm", "auto", "bitand", "bitor", "char16_t", "char32_t", "compl",
        "concept", "consteval", "constexpr", "constinit", "const_cast", "decltype", "delete", "dynamic_cast", "export",
        "friend", "inline", "mutable", "noexcept", "nullptr", "or", "or_eq", "reflexpr", "register", "reinterpret_cast",
        "requires", "signed", "static_assert", "static_cast", "template", "thread_local", "typedef", "typeid", "typename",
        "union", "unsigned", "wchar_t", "while", "xor", "xor_eq",
        // Java
        "assert", "boolean", "extends", "final", "implements", "import", "instanceof", "native", "package", "strictfp",
        "super", "synchronized", "throws", "transient",
        // TypeScript
        "any", "debugger", "declare", "from", "function", "keyof", "let", "module", "never", "number", "require",
        "symbol", "type", "undefined", "unique", "unknown", "var", "with", "yield",
        // Rust
        "async", "await", "become", "box", "crate", "dyn", "fn", "impl", "loop", "macro", "match", "mod", "move", "mut",
        "priv", "pub", "self", "Self", "trait", "use", "where",
        // Go
        "chan", "defer", "fallthrough", "func", "go", "range", "select",
        // special cases across languages
        "arguments", "eval",
    ];
}
