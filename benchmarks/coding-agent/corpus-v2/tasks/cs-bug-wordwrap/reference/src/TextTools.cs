using System;
using System.Text;

public static class TextTools
{
    public static string Wrap(string text, int width)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        var words = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        int col = 0;
        foreach (var word in words)
        {
            if (col == 0)
            {
                sb.Append(word);
                col = word.Length;
            }
            else if (col + 1 + word.Length <= width)
            {
                sb.Append(' ').Append(word);
                col += 1 + word.Length;
            }
            else
            {
                sb.Append('\n').Append(word);
                col = word.Length;
            }
        }
        return sb.ToString();
    }
}
