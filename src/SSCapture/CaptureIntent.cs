namespace SSCapture;

public enum CaptureIntent { Screenshot, Recording }
[Flags]
public enum OutputDestination { None = 0, Clipboard = 1, File = 2, ExternalApp = 4 }
