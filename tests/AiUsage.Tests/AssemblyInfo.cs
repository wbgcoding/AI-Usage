// Tests run beside each other by default. The process-wide state a handful of classes legitimately
// touch (a WPF Application on its own STA thread, the shared LocalizationService.Instance) is kept
// safe instead by grouping exactly those classes into their own xunit collection - see
// TestPathsCleanupFixture.cs - rather than serialising the whole suite for their sake.
