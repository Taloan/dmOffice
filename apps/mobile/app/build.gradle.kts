/**
 * Firebase-Werte aus google-services.json (Firebase-Konsole -> Projekteinstellungen -> Android-App).
 * Ohne Datei bleiben die Felder leer und Push ist aus. Kein google-services-Plugin, damit der Build
 * auch ohne die Datei durchlaeuft.
 */
fun firebaseValues(packageName: String): Map<String, String> {
    val f = file("google-services.json")
    if (!f.exists()) return emptyMap()
    @Suppress("UNCHECKED_CAST")
    val root = groovy.json.JsonSlurper().parse(f) as Map<String, Any?>
    val info = root["project_info"] as Map<String, Any?>
    val clients = root["client"] as List<Map<String, Any?>>
    val client = clients.firstOrNull { ((it["client_info"] as Map<*, *>)["android_client_info"] as Map<*, *>)["package_name"] == packageName } ?: return emptyMap()
    val appId = (client["client_info"] as Map<*, *>)["mobilesdk_app_id"] as String
    val apiKey = ((client["api_key"] as List<Map<*, *>>).first()["current_key"]) as String
    return mapOf("FCM_APP_ID" to appId, "FCM_API_KEY" to apiKey, "FCM_PROJECT_ID" to (info["project_id"] as String), "FCM_SENDER_ID" to (info["project_number"] as String))
}

fun com.android.build.api.dsl.VariantDimension.firebaseFields(packageName: String) {
    val v = firebaseValues(packageName)
    for (k in listOf("FCM_APP_ID", "FCM_API_KEY", "FCM_PROJECT_ID", "FCM_SENDER_ID")) buildConfigField("String", k, "\"" + (v[k] ?: "") + "\"")
}

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.ksp)
    alias(libs.plugins.kotlin.serialization)
}

android {
    namespace = "at.dimes.dmoffice"
    compileSdk = 36

    defaultConfig {
        applicationId = "at.dimes.dmoffice"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "0.1"
        vectorDrawables.useSupportLibrary = true
    }

    buildTypes {
        debug {
            applicationIdSuffix = ".debug"
            firebaseFields("at.dimes.dmoffice.debug")
        }
        release {
            firebaseFields("at.dimes.dmoffice")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
        isCoreLibraryDesugaringEnabled = false
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures { compose = true; buildConfig = true }
    packaging { resources.excludes += "/META-INF/{AL2.0,LGPL2.1}" }
}

ksp { arg("room.schemaLocation", "$projectDir/schemas") }

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.compose.material.icons)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.navigation.compose)
    implementation(libs.androidx.room.runtime)
    implementation(libs.androidx.room.ktx)
    ksp(libs.androidx.room.compiler)
    implementation(libs.androidx.work.runtime)
    implementation(libs.androidx.glance.appwidget)
    implementation(libs.androidx.glance.material3)
    implementation(libs.androidx.security.crypto)
    implementation(libs.okhttp)
    implementation(libs.kotlinx.coroutines.android)
    implementation(libs.zxing.embedded)
    implementation(libs.kotlinx.serialization.json)
    implementation(platform(libs.firebase.bom))
    implementation(libs.firebase.messaging)
    implementation(libs.kotlinx.coroutines.play.services)
    implementation(libs.sqlcipher)
    implementation(libs.androidx.sqlite)
    implementation(libs.androidx.biometric)
    implementation(libs.androidx.fragment)
    implementation(libs.androidx.webkit)
    testImplementation(libs.junit)
    testImplementation(libs.kxml2)
    debugImplementation(libs.androidx.compose.ui.tooling)
}
