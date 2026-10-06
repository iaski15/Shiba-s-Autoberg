/**
 * Steamless - Copyright (c) 2015 - 2024 atom0s [atom0s@live.com]
 *
 * This work is licensed under the Creative Commons Attribution-NonCommercial-NoDerivatives 4.0 International License.
 * To view a copy of this license, visit http://creativecommons.org/licenses/by-nc-nd/4.0/ or send a letter to
 * Creative Commons, PO Box 1866, Mountain View, CA 94042, USA.
 *
 * By using Steamless, you agree to the above license and its terms.
 *
 *      Attribution - You must give appropriate credit, provide a link to the license and indicate if changes were
 *                    made. You must do so in any reasonable manner, but not in any way that suggests the licensor
 *                    endorses you or your use.
 *
 *   Non-Commercial - You may not use the material (Steamless) for commercial purposes.
 *
 *   No-Derivatives - If you remix, transform, or build upon the material (Steamless), you may not distribute the
 *                    modified material. You are, however, allowed to submit the modified works back to the original
 *                    Steamless project in attempt to have it added to the original project.
 *
 * You may not apply legal terms or technological measures that legally restrict others
 * from doing anything the license permits.
 *
 * No warranties are given.
 */

namespace Shibaless.API.Model
{
    // GOLDBERG PATCH (in-memory-output): System/System.IO for the output hook below.
    using System;
    using System.IO;

    public class ShibalessOptions : NotifiableModel
    {
        /// <summary>
        /// Default Constructor
        /// </summary>
        public ShibalessOptions()
        {
            this.VerboseOutput = true;
            this.KeepBindSection = false;
            this.DumpPayloadToDisk = false;
            this.DumpSteamDrmpToDisk = false;
            this.UseExperimentalFeatures = false;
            this.DontRealignSections = true;
            this.ZeroDosStubData = true;
            this.RecalculateFileChecksum = false;
        }

        // GOLDBERG PATCH (in-memory-output): lets a host keep the rebuilt image in memory instead of
        // having it written to "<file>.unpacked.exe". Null (the default) keeps upstream behaviour.
        // Not combinable with RecalculateFileChecksum, which re-opens the output by path.
        public Func<string, Stream> OutputStreamFactory { get; set; }

        // GOLDBERG PATCH (in-memory-output): what every unpacker now calls to open its output file.
        public Stream OpenOutputFile(string unpackedPath)
        {
            return this.OutputStreamFactory?.Invoke(unpackedPath) ?? new FileStream(unpackedPath, FileMode.Create, FileAccess.ReadWrite);
        }

        /// <summary>
        /// Gets or sets the verbose output option value.
        /// </summary>
        public bool VerboseOutput
        {
            get => this.Get<bool>("VerboseOutput");
            set => this.Set("VerboseOutput", value);
        }

        /// <summary>
        /// Gets or sets the keep bind section option value.
        /// </summary>
        public bool KeepBindSection
        {
            get => this.Get<bool>("KeepBindSection");
            set => this.Set("KeepBindSection", value);
        }

        /// <summary>
        /// Gets or sets the dump payload to disk option value.
        /// </summary>
        public bool DumpPayloadToDisk
        {
            get => this.Get<bool>("DumpPayloadToDisk");
            set => this.Set("DumpPayloadToDisk", value);
        }

        /// <summary>
        /// Gets or sets the dump SteamDRMP.dll to disk option value.
        /// </summary>
        public bool DumpSteamDrmpToDisk
        {
            get => this.Get<bool>("DumpSteamDrmpToDisk");
            set => this.Set("DumpSteamDrmpToDisk", value);
        }

        /// <summary>
        /// Gets or sets the use experimental features option value.
        /// </summary>
        public bool UseExperimentalFeatures
        {
            get => this.Get<bool>("UseExperimentalFeatures");
            set => this.Set("UseExperimentalFeatures", value);
        }

        /// <summary>
        /// Gets or sets the don't realign sections option value.
        /// </summary>
        public bool DontRealignSections
        {
            get => this.Get<bool>("DontRealignSections");
            set => this.Set("DontRealignSections", value);
        }

        /// <summary>
        /// Gets or sets if the DOS stub data should be zeroed.
        /// </summary>
        public bool ZeroDosStubData
        {
            get => this.Get<bool>("ZeroDosStubData");
            set => this.Set("ZeroDosStubData", value);
        }

        /// <summary>
        /// Gets or sets if the file checksum should be recalculated.
        /// </summary>
        public bool RecalculateFileChecksum
        {
            get => this.Get<bool>("RecalculateFileChecksum");
            set => this.Set("RecalculateFileChecksum", value);
        }
    }
}