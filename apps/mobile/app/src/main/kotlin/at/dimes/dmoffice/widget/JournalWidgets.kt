package at.dimes.dmoffice.widget

import android.content.Context
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.ColorFilter
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Column
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextAlign
import androidx.glance.text.TextStyle
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R

/**
 * Kleine Knöpfe für den Startbildschirm: neuer Tagebucheintrag - mit Foto (öffnet gleich die Kamera; Foto, Datum, Uhrzeit und
 * Ort hängen dann am Eintrag) oder ohne (Datum, Uhrzeit, Standort). Beides öffnet den Editor der App (Route journal/new).
 */
abstract class JournalButtonWidget(private val icon: Int, private val label: Int, private val start: String) : GlanceAppWidget() {
    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val open = MainActivity.routeIntent(context, "journal/new?start=$start")
        provideContent {
            GlanceTheme(colors = widgetColors(context)) {
                Column(
                    modifier = GlanceModifier.fillMaxSize().cornerRadius(20.dp).background(GlanceTheme.colors.primary).padding(6.dp).clickable(actionStartActivity(open)),
                    horizontalAlignment = Alignment.CenterHorizontally, verticalAlignment = Alignment.CenterVertically,
                ) {
                    Image(ImageProvider(icon), contentDescription = null, modifier = GlanceModifier.size(30.dp), colorFilter = ColorFilter.tint(GlanceTheme.colors.onPrimary))
                    Text(context.getString(label), maxLines = 2,
                        style = TextStyle(color = GlanceTheme.colors.onPrimary, fontSize = 12.sp, fontWeight = FontWeight.Medium, textAlign = TextAlign.Center))
                }
            }
        }
    }
}

class JournalPhotoWidget : JournalButtonWidget(R.drawable.ic_journal_photo, R.string.widget_journal_photo, "camera")
class JournalPhotoWidgetReceiver : GlanceAppWidgetReceiver() { override val glanceAppWidget: GlanceAppWidget = JournalPhotoWidget() }

class JournalNewWidget : JournalButtonWidget(R.drawable.ic_journal_new, R.string.widget_journal_new, "new")
class JournalNewWidgetReceiver : GlanceAppWidgetReceiver() { override val glanceAppWidget: GlanceAppWidget = JournalNewWidget() }
