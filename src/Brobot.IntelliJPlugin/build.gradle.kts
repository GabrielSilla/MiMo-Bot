plugins {
    java
    id("org.jetbrains.intellij.platform") version "2.10.2"
}

group = "br.com.brobot"
version = "1.0.0"

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

java {
    toolchain {
        languageVersion.set(JavaLanguageVersion.of(21))
    }
}

// Compiles against the IntelliJ the developer already has installed when
// -PideaPath=<dir> (or build-installer.ps1) points at it; otherwise downloads
// IntelliJ IDEA from JetBrains' repository, which is ~1.5GB the first time.
val ideaPath: String? = providers.gradleProperty("ideaPath").orNull

dependencies {
    intellijPlatform {
        if (ideaPath != null) {
            local(ideaPath)
        } else {
            intellijIdea("2025.3.1")
        }
        bundledPlugin("com.intellij.gradle")
    }
}

intellijPlatform {
    pluginConfiguration {
        name = "Peemo Build Watcher"
        ideaVersion {
            sinceBuild = "253"
            untilBuild = provider { null }
        }
    }
    buildSearchableOptions = false
}
