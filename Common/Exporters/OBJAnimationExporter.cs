using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTK;
using PSXPrev.Common.Animator;

namespace PSXPrev.Common.Exporters
{
    /// <summary>
    /// Exports animation frames as separate OBJ files.
    /// Each frame of an animation is exported as an individual OBJ mesh,
    /// allowing for frame-by-frame model inspection and 3D animation baking.
    /// </summary>
    public class OBJAnimationExporter
    {
        private ExportModelOptions _options;
        private string _baseName;
        private string _animationName;

        /// <summary>
        /// Exports all frames of an animation as separate OBJ files in a single folder.
        /// Files are named with sequential frame numbers (e.g., anim_frame_0000.obj, anim_frame_0001.obj).
        /// </summary>
        /// <param name="options">Export options including path and naming settings</param>
        /// <param name="entities">Root entities to export with animation applied</param>
        /// <param name="animation">The animation to export frames from</param>
        /// <returns>Total number of OBJ files exported</returns>
        public int ExportFramesSingleFolder(ExportModelOptions options, RootEntity[] entities, Animation animation)
        {
            if (animation == null || animation.FrameCount == 0)
            {
                Program.Logger.WriteWarningLine("Animation is null or has no frames. Skipping OBJ animation export.");
                return 0;
            }

            _options = options?.Clone() ?? new ExportModelOptions();
            _options.Validate("obj");
            _baseName = _options.Name;
            _animationName = animation.Name ?? "animation";

            int fileCount = 0;
            int frameCount = (int)animation.FrameCount;

            Program.Logger.WriteLine($"Exporting {frameCount} animation frames as OBJ files to: {_options.Path}");

            // Iterate through each frame
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                try
                {
                    // Create animation batch and set to specific frame
                    var animBatch = new AnimationBatch();
                    animBatch.SetupAnimationBatch(animation);
                    animBatch.FrameTime = frameIndex; // Set frame number

                    // Set up animation state for all entities at this frame
                    foreach (var entity in entities)
                    {
                        if (entity != null)
                        {
                            animBatch.SetupAnimationFrame(entity, force: true);
                        }
                    }

                    // Export this frame with frame number appended to filename
                    var frameOptions = _options.Clone();
                    frameOptions.Path = _options.Path;
                    frameOptions.Name = $"{_baseName}_frame_{frameIndex:D4}";

                    var objExporter = new OBJExporter();
                    int frameFileCount = objExporter.Export(frameOptions, entities);
                    fileCount += frameFileCount;

                    if (frameIndex % 10 == 0 || frameIndex == frameCount - 1)
                    {
                        Program.Logger.WriteLine($"  Exported frame {frameIndex + 1}/{frameCount}");
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger.WriteExceptionLine(ex, $"Error exporting animation frame {frameIndex}");
                }
            }

            Program.Logger.WritePositiveLine($"Completed OBJ animation export: {fileCount} files created");
            return fileCount;
        }

        /// <summary>
        /// Exports animation frames to separate subdirectories (one per frame).
        /// Useful for keeping each frame's complete geometry and texture data isolated.
        /// </summary>
        /// <param name="options">Export options including path and naming settings</param>
        /// <param name="entities">Root entities to export with animation applied</param>
        /// <param name="animation">The animation to export frames from</param>
        /// <returns>Total number of OBJ files exported</returns>
        public int ExportFramesPerDirectory(ExportModelOptions options, RootEntity[] entities, Animation animation)
        {
            if (animation == null || animation.FrameCount == 0)
            {
                Program.Logger.WriteWarningLine("Animation is null or has no frames. Skipping OBJ animation export.");
                return 0;
            }

            _options = options?.Clone() ?? new ExportModelOptions();
            _options.Validate("obj");
            _baseName = _options.Name;
            _animationName = animation.Name ?? "animation";

            int fileCount = 0;
            int frameCount = (int)animation.FrameCount;

            // Create parent animation folder
            string animFolder = Path.Combine(_options.Path, $"{_baseName}_{_animationName}");
            Directory.CreateDirectory(animFolder);

            Program.Logger.WriteLine($"Exporting {frameCount} animation frames to subdirectories: {animFolder}");

            // Iterate through each frame
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                try
                {
                    // Create frame-specific subdirectory
                    string frameFolder = Path.Combine(animFolder, $"frame_{frameIndex:D4}");
                    Directory.CreateDirectory(frameFolder);

                    // Create animation batch and set to specific frame
                    var animBatch = new AnimationBatch();
                    animBatch.SetupAnimationBatch(animation);
                    animBatch.FrameTime = frameIndex; // Set frame number

                    // Set up animation state for all entities at this frame
                    foreach (var entity in entities)
                    {
                        if (entity != null)
                        {
                            animBatch.SetupAnimationFrame(entity, force: true);
                        }
                    }

                    // Export this frame to its own directory
                    var frameOptions = _options.Clone();
                    frameOptions.Path = frameFolder;
                    frameOptions.Name = $"{_baseName}_frame_{frameIndex:D4}";

                    var objExporter = new OBJExporter();
                    int frameFileCount = objExporter.Export(frameOptions, entities);
                    fileCount += frameFileCount;

                    if (frameIndex % 10 == 0 || frameIndex == frameCount - 1)
                    {
                        Program.Logger.WriteLine($"  Exported frame {frameIndex + 1}/{frameCount}");
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger.WriteExceptionLine(ex, $"Error exporting animation frame {frameIndex}");
                }
            }

            Program.Logger.WritePositiveLine($"Completed OBJ animation export: {fileCount} files created in {frameCount} frame directories");
            return fileCount;
        }

        /// <summary>
        /// Exports animation frames with frame stepping (export every Nth frame).
        /// Useful for long animations to reduce file count and processing time.
        /// </summary>
        /// <param name="options">Export options including path and naming settings</param>
        /// <param name="entities">Root entities to export with animation applied</param>
        /// <param name="animation">The animation to export frames from</param>
        /// <param name="frameStep">Export every Nth frame (1 = all frames, 2 = every other frame, etc.)</param>
        /// <returns>Total number of OBJ files exported</returns>
        public int ExportFramesWithStep(ExportModelOptions options, RootEntity[] entities, Animation animation, int frameStep = 1)
        {
            if (animation == null || animation.FrameCount == 0)
            {
                Program.Logger.WriteWarningLine("Animation is null or has no frames. Skipping OBJ animation export.");
                return 0;
            }

            if (frameStep < 1)
            {
                frameStep = 1;
            }

            _options = options?.Clone() ?? new ExportModelOptions();
            _options.Validate("obj");
            _baseName = _options.Name;
            _animationName = animation.Name ?? "animation";

            int fileCount = 0;
            int frameCount = (int)animation.FrameCount;
            int exportedFrames = 0;

            Program.Logger.WriteLine($"Exporting animation frames (step={frameStep}) to: {_options.Path}");

            // Iterate through frames with step
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex += frameStep)
            {
                try
                {
                    // Create animation batch and set to specific frame
                    var animBatch = new AnimationBatch();
                    animBatch.SetupAnimationBatch(animation);
                    animBatch.FrameTime = frameIndex; // Set frame number

                    // Set up animation state for all entities at this frame
                    foreach (var entity in entities)
                    {
                        if (entity != null)
                        {
                            animBatch.SetupAnimationFrame(entity, force: true);
                        }
                    }

                    // Export this frame
                    var frameOptions = _options.Clone();
                    frameOptions.Path = _options.Path;
                    frameOptions.Name = $"{_baseName}_frame_{frameIndex:D4}";

                    var objExporter = new OBJExporter();
                    int frameFileCount = objExporter.Export(frameOptions, entities);
                    fileCount += frameFileCount;
                    exportedFrames++;

                    if (exportedFrames % 10 == 0)
                    {
                        Program.Logger.WriteLine($"  Exported {exportedFrames} frames...");
                    }
                }
                catch (Exception ex)
                {
                    Program.Logger.WriteExceptionLine(ex, $"Error exporting animation frame {frameIndex}");
                }
            }

            Program.Logger.WritePositiveLine($"Completed OBJ animation export: {fileCount} files created ({exportedFrames} frames with step {frameStep})");
            return fileCount;
        }
    }
}
