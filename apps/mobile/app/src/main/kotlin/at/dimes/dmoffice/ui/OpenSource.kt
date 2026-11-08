package at.dimes.dmoffice.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import at.dimes.dmoffice.R
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/** Eintrag aus assets/third-party.json (Gradle-Task ossLizenzen, siehe app/build.gradle.kts). */
private class OssEntry(val name: String, val version: String?, val license: String, val url: String?, val packages: List<String>, val texts: List<String>)

private fun loadOss(ctx: android.content.Context): Pair<List<OssEntry>, Map<String, String>> = runCatching {
    val root = Json.parseToJsonElement(ctx.assets.open("third-party.json").bufferedReader().use { it.readText() }).jsonObject
    fun JsonObject.str(k: String) = this[k]?.jsonPrimitive?.content
    fun JsonObject.list(k: String) = this[k]?.jsonArray?.map { it.jsonPrimitive.content } ?: emptyList()
    val entries = root["sections"]!!.jsonArray.flatMap { it.jsonObject["entries"]!!.jsonArray }.map { it.jsonObject }
        .map { OssEntry(it.str("name") ?: "", it.str("version"), it.str("license") ?: "", it.str("url"), it.list("packages"), it.list("texts")) }
    val texts = root["texts"]?.jsonObject?.mapValues { it.value.jsonPrimitive.content } ?: emptyMap()
    entries to texts
}.getOrDefault(emptyList<OssEntry>() to emptyMap())

/** Einstellungen → Über → Open-Source-Bibliotheken: Dank, Liste mit Lizenz und Link, Lizenztexte zum Aufklappen. */
@Composable
fun OpenSourceList() {
    val ctx = LocalContext.current
    val uri = LocalUriHandler.current
    val (entries, texts) = remember { loadOss(ctx) }
    var open by rememberSaveable { mutableStateOf<String?>(null) }
    val muted = MaterialTheme.colorScheme.onSurfaceVariant
    Column(Modifier.padding(horizontal = 16.dp)) {
        Text(stringResource(R.string.oss_thanks), style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(vertical = 8.dp))
        Text(stringResource(R.string.oss_more), style = MaterialTheme.typography.bodySmall, color = muted, modifier = Modifier.padding(bottom = 4.dp))
        Text(stringResource(R.string.oss_google), style = MaterialTheme.typography.bodySmall, color = muted, modifier = Modifier.padding(bottom = 8.dp))
        for (e in entries) {
            val key = e.name + "|" + e.license
            val expanded = open == key
            Row(Modifier.fillMaxWidth().clickable { open = if (expanded) null else key }.padding(vertical = 8.dp), verticalAlignment = Alignment.Top) {
                Icon(if (expanded) Icons.Filled.KeyboardArrowDown else Icons.Filled.KeyboardArrowRight, contentDescription = null, tint = muted)
                Column(Modifier.weight(1f).padding(start = 6.dp)) {
                    Text(e.name, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold)
                    Text(listOfNotNull(e.version, e.license, if (e.packages.size > 1) stringResource(R.string.oss_packages, e.packages.size) else null).joinToString(" · "),
                        style = MaterialTheme.typography.bodySmall, color = muted)
                }
            }
            if (expanded) Column(Modifier.padding(start = 30.dp, bottom = 8.dp)) {
                e.url?.let { u -> Text(u.removePrefix("https://").removePrefix("http://"), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.clickable { runCatching { uri.openUri(u) } }.padding(vertical = 4.dp)) }
                if (e.packages.size > 1) Text(e.packages.joinToString(", "), style = MaterialTheme.typography.bodySmall, color = muted, modifier = Modifier.padding(vertical = 4.dp))
                for (k in e.texts) texts[k]?.let { t ->
                    Text(t, fontFamily = FontFamily.Monospace, fontSize = 10.sp, lineHeight = 13.sp,
                        modifier = Modifier.padding(vertical = 4.dp).fillMaxWidth().heightIn(max = 320.dp)
                            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(6.dp)).verticalScroll(rememberScrollState()).padding(8.dp))
                }
                if (e.texts.isEmpty()) Text(stringResource(R.string.oss_text_at_url), style = MaterialTheme.typography.bodySmall, color = muted)
            }
        }
    }
}
