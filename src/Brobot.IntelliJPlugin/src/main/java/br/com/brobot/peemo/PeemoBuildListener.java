package br.com.brobot.peemo;

import com.intellij.openapi.externalSystem.model.task.ExternalSystemTaskId;
import com.intellij.openapi.externalSystem.model.task.ExternalSystemTaskNotificationListener;
import com.intellij.openapi.externalSystem.model.task.ExternalSystemTaskType;
import com.intellij.openapi.project.Project;
import org.jetbrains.annotations.NotNull;

import java.io.IOException;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.nio.file.Path;
import java.nio.file.Paths;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Reports a Gradle task execution starting/finishing in IntelliJ to Peemo
 * Sender -- the IntelliJ counterpart of Brobot.VSExtension's
 * BrobotBuildWatcherPackage, over the exact same wire: one plain-text line,
 * "EVENTNAME optional text...", to a fresh TCP connection on 127.0.0.1:5591
 * (AiThoughtsListener), then closes.
 *
 * Runs in-process inside the IDE, so unlike tailing the Gradle daemon's log
 * it sees exactly the builds the IDE itself runs, with the project's own name.
 * Never throws out of a callback and never does network I/O on the IDE's
 * threads: the write happens on a single background thread, and any failure
 * (Peemo Sender not running, nothing listening) is swallowed -- this must
 * never be the thing that makes an IntelliJ build feel slow or broken.
 */
public final class PeemoBuildListener implements ExternalSystemTaskNotificationListener {
    private static final int PORT = 5591;
    private static final String GRADLE_SYSTEM_ID = "GRADLE";

    // Daemon thread so a pending write can never keep the IDE from exiting.
    private static final ExecutorService SENDER = Executors.newSingleThreadExecutor(runnable -> {
        Thread thread = new Thread(runnable, "Peemo-BuildWatcher-Sender");
        thread.setDaemon(true);
        return thread;
    });

    @Override
    public void onStart(@NotNull String projectPath, @NotNull ExternalSystemTaskId id) {
        if (isGradleBuild(id)) {
            send("IdeBuildStarted", projectName(projectPath, id));
        }
    }

    @Override
    public void onSuccess(@NotNull String projectPath, @NotNull ExternalSystemTaskId id) {
        if (isGradleBuild(id)) {
            send("IdeBuildSucceeded", projectName(projectPath, id));
        }
    }

    @Override
    public void onFailure(@NotNull String projectPath, @NotNull ExternalSystemTaskId id, @NotNull Exception exception) {
        if (isGradleBuild(id)) {
            send("IdeBuildFailed", projectName(projectPath, id));
        }
    }

    // Only "run a Gradle task" -- not project resolve/import/sync, which
    // fire the same callbacks but are not builds.
    private static boolean isGradleBuild(ExternalSystemTaskId id) {
        return id.getType() == ExternalSystemTaskType.EXECUTE_TASK
                && GRADLE_SYSTEM_ID.equals(id.getProjectSystemId().getId());
    }

    private static String projectName(String projectPath, ExternalSystemTaskId id) {
        try {
            Project project = id.findProject();
            if (project != null && !project.getName().isBlank()) {
                return project.getName();
            }
            Path leaf = Paths.get(projectPath).getFileName();
            return leaf == null ? "" : leaf.toString();
        } catch (RuntimeException e) {
            return "";
        }
    }

    private static void send(String eventName, String text) {
        // Newlines would end the line early on the Sender's side.
        String line = (text == null || text.isBlank() ? eventName : eventName + " " + text.replaceAll("[\\r\\n]+", " ")) + "\n";
        SENDER.execute(() -> {
            try (Socket socket = new Socket()) {
                socket.connect(new InetSocketAddress("127.0.0.1", PORT), 1000);
                OutputStream out = socket.getOutputStream();
                out.write(line.getBytes(StandardCharsets.UTF_8));
                out.flush();
            } catch (IOException ignored) {
                // Peemo Sender isn't running / isn't listening -- nothing to do.
            }
        });
    }
}
