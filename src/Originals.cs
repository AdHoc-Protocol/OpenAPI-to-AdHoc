using System;
using System.Collections.Generic;
using System.IO;

namespace org.unirail;

/// <summary>
/// Where an input file comes from. `fetch-samples.sh` writes `sources.txt` next to the files it downloads, one
/// line per file: `<path relative to that folder> <page of the original>`; a path that ends with `/` stands for
/// every file under that folder, the rest of the path is added to the page. The header of a description links
/// the originals it was made from. A file with no such list in a folder above it gets no link.
/// </summary>
static class Originals{
    public const string LIST = "sources.txt";

    static readonly Dictionary<string, List<(string path, string page)>?> lists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The page of the original of <paramref name="file"/>, or null. The nearest list above the file decides.</summary>
    public static string? of(string file)
    {
        if( file == "" || !File.Exists(file) ) return null;
        file = Path.GetFullPath(file);
        for( var dir = Path.GetDirectoryName(file); dir != null; dir = Path.GetDirectoryName(dir) )
        {
            if( read(dir) is not { } list ) continue;
            var    path  = Path.GetRelativePath(dir, file).Replace('\\', '/');
            string? page = null;
            var    best  = -1;
            foreach( var (p, url) in list )
                if( best < p.Length && (p == path || p.EndsWith('/') && path.StartsWith(p, StringComparison.Ordinal)) )
                {
                    page = p == path ? url : url + path[p.Length..];
                    best = p.Length;
                }
            return page;
        }
        return null;
    }

    static List<(string, string)>? read(string dir)
    {
        if( lists.TryGetValue(dir, out var known) ) return known;
        List<(string, string)>? list = null;
        var                     file = Path.Combine(dir, LIST);
        if( File.Exists(file) )
        {
            list = [];
            foreach( var line in File.ReadLines(file) )
            {
                var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if( words.Length == 2 && words[0][0] != '#' && (words[1].StartsWith("https://") || words[1].StartsWith("http://")) )
                    list.Add((words[0].Replace('\\', '/'), words[1]));
            }
        }
        return lists[dir] = list;
    }
}
