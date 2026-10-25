using System;
using System.Text;

public static class TextTools
{
    public static string Wrap(string text, int width)
    {
        var sb = new StringBuilder();
        int col = 0;
        foreach (var word in text.Split(' '))
        {
            if (col + word.Length > width)
            {
                sb.Append('\n');
                col = 0;
            }
            sb.Append(word).Append(' ');
            col += word.Length + 1;
        }
        return sb.ToString();
    }
}
