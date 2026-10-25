package com.parux.android.ui.chat

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp

/**
 * A deliberately lightweight Markdown renderer: fenced code blocks get a monospace card,
 * everything else gets bold/italic/inline-code spans. It covers what chat replies actually use
 * in practice; it is not a full CommonMark implementation, matching the scope of Windows's own
 * MarkdownRenderer.cs (block + inline parsing, not a general-purpose parser).
 */
@Composable
fun MarkdownText(text: String, modifier: Modifier = Modifier) {
    Column(modifier) {
        val blocks = splitFencedBlocks(text)
        blocks.forEach { block ->
            when (block) {
                is Block.Code -> CodeBlock(block.language, block.content)
                is Block.Text -> if (block.content.isNotBlank()) {
                    Text(
                        text = renderInline(block.content),
                        style = MaterialTheme.typography.bodyLarge,
                        modifier = Modifier.padding(vertical = 2.dp)
                    )
                }
            }
        }
    }
}

@Composable
private fun CodeBlock(language: String, content: String) {
    Column(
        Modifier
            .padding(vertical = 6.dp)
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(8.dp))
            .padding(12.dp)
    ) {
        if (language.isNotBlank()) {
            Text(language, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Text(content, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodyMedium)
    }
}

private sealed interface Block {
    data class Text(val content: String) : Block
    data class Code(val language: String, val content: String) : Block
}

private fun splitFencedBlocks(text: String): List<Block> {
    val blocks = mutableListOf<Block>()
    val lines = text.split("\n")
    var i = 0
    val current = StringBuilder()
    while (i < lines.size) {
        val line = lines[i]
        if (line.trimStart().startsWith("```")) {
            if (current.isNotEmpty()) { blocks.add(Block.Text(current.toString())); current.clear() }
            val language = line.trimStart().removePrefix("```").trim()
            val codeLines = mutableListOf<String>()
            i++
            while (i < lines.size && !lines[i].trimStart().startsWith("```")) {
                codeLines.add(lines[i])
                i++
            }
            blocks.add(Block.Code(language, codeLines.joinToString("\n")))
            i++ // skip closing fence (or, for an unclosed fence mid-stream, the loop already ended)
            continue
        }
        current.append(line).append("\n")
        i++
    }
    if (current.isNotEmpty()) blocks.add(Block.Text(current.toString()))
    return blocks
}

private val boldRx = Regex("\\*\\*(.+?)\\*\\*")
private val italicRx = Regex("(?<![*\\w])\\*(?!\\s)(.+?)(?<!\\s)\\*(?!\\*)")
private val inlineCodeRx = Regex("`([^`]+)`")

private fun renderInline(text: String) = buildAnnotatedString {
    var index = 0
    // Merge all inline matches in order of appearance, then apply whichever wins at each position.
    val matches = (boldRx.findAll(text).map { Triple(it.range, "bold", it.groupValues[1]) } +
        inlineCodeRx.findAll(text).map { Triple(it.range, "code", it.groupValues[1]) } +
        italicRx.findAll(text).map { Triple(it.range, "italic", it.groupValues[1]) })
        .sortedBy { it.first.first }
        .fold(mutableListOf<Triple<IntRange, String, String>>()) { acc, m ->
            if (acc.none { it.first.first <= m.first.first && m.first.first <= it.first.last }) acc.add(m)
            acc
        }

    for (match in matches) {
        if (match.first.first > index) append(text.substring(index, match.first.first))
        when (match.second) {
            "bold" -> withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append(match.third) }
            "italic" -> withStyle(SpanStyle(fontStyle = FontStyle.Italic)) { append(match.third) }
            "code" -> withStyle(SpanStyle(fontFamily = FontFamily.Monospace)) { append(match.third) }
        }
        index = match.first.last + 1
    }
    if (index < text.length) append(text.substring(index))
}
