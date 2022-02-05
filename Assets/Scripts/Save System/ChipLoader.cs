﻿using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public static class ChipLoader {

    public static SavedChip[] GetAllSavedChips(string[] chipPaths) {
        SavedChip[] savedChips = new SavedChip[chipPaths.Length];

        // Read saved chips from file
        for (int i = 0; i < chipPaths.Length; i++) {
            string chipSaveString = SaveSystem.ReadFile(chipPaths[i]);

            // If the save does not contain wireType and contains outputPinNames then its a previous version save (v 0.25 expected)
            if (!chipSaveString.Contains("wireType") || chipSaveString.Contains("outputPinNames") || !chipSaveString.Contains("outputPins")) {

                // An update is made to the save string and returned
                string updatedSave = SaveCompatibility.FixSaveCompatibility(chipSaveString);
                savedChips[i] = JsonUtility.FromJson<SavedChip>(updatedSave);
            } else {
                savedChips[i] = JsonUtility.FromJson<SavedChip>(chipSaveString);
            }
        }

        foreach (SavedChip chip in savedChips) {
            if (String.IsNullOrEmpty(chip.folderName)) {
                chip.folderName = "User";
            }
            if (float.IsNaN(chip.scale)) {
                chip.scale = 1f;
            }
        }
        return savedChips;
    }

    public static async void LoadAllChips (string[] chipPaths, Manager manager) {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        SavedChip[] savedChips = GetAllSavedChips(chipPaths);
        ProgressBar progressBar = ProgressBar.New("Loading All Chips...", wholeNumbers: true);
        progressBar.Open(0, savedChips.Length + manager.builtinChips.Length);
        progressBar.SetValue(0, "Start Loading...");

        SortChipsByOrderOfCreation (ref savedChips);
        // Maintain dictionary of loaded chips (initially just the built-in chips)
        Dictionary<string, Chip> loadedChips = new Dictionary<string, Chip> ();
        for (int i = 0; i < manager.builtinChips.Length; i++) {
            Chip builtinChip = manager.builtinChips[i];
            progressBar.SetValue(i, "Loading '" + builtinChip.chipName + "'...");
            loadedChips.Add (builtinChip.chipName, builtinChip);
            await Task.Yield();
        }
        for (int i = 0; i < savedChips.Length; i++) {
            SavedChip chipToTryLoad = savedChips[i];
            progressBar.SetValue(i + manager.builtinChips.Length, "Loading '" + chipToTryLoad.name + "'...");
            try {
                ChipSaveData loadedChipData = LoadChip (chipToTryLoad, loadedChips, manager.wirePrefab);
                Chip loadedChip = manager.LoadChip (loadedChipData);
                loadedChips.Add (loadedChip.chipName, loadedChip);
            } catch(Exception e) {
                DLSLogger.LogWarning("Custom Chip '" + chipToTryLoad.name + "' could not be loaded!", e.ToString());
            }
            await Task.Yield();
        }
        progressBar.SetValue(progressBar.progressBar.maxValue, "Done!");
        progressBar.Close();
        DLSLogger.Log("Load time: " + sw.ElapsedMilliseconds.ToString() + "ms");
    }

    // Instantiates all components that make up the given chip, and connects them up with wires
    // The components are parented under a single "holder" object, which is returned from the function
    static ChipSaveData LoadChip (SavedChip chipToLoad, Dictionary<string, Chip> previouslyLoadedChips, Wire wirePrefab) {
        ChipSaveData loadedChipData = new ChipSaveData ();
        int numComponents = chipToLoad.savedComponentChips.Length;
        loadedChipData.componentChips = new Chip[numComponents];
        loadedChipData.chipName = chipToLoad.name;
        loadedChipData.chipColour = chipToLoad.colour;
        loadedChipData.chipNameColour = chipToLoad.nameColour;
        loadedChipData.creationIndex = chipToLoad.creationIndex;
        loadedChipData.folderName = chipToLoad.folderName;
        loadedChipData.scale = chipToLoad.scale;

        // Spawn component chips (the chips used to create this chip)
        // These will have been loaded already, and stored in the previouslyLoadedChips dictionary
        for (int i = 0; i < numComponents; i++) {
            SavedComponentChip componentToLoad = chipToLoad.savedComponentChips[i];
            string componentName = componentToLoad.chipName;
            Vector2 pos = new Vector2 ((float) componentToLoad.posX, (float) componentToLoad.posY);

            if (!previouslyLoadedChips.ContainsKey (componentName)) {
                DLSLogger.LogError ("Failed to load sub component: " + componentName + " While loading " + chipToLoad.name);
            }

            Chip loadedComponentChip = GameObject.Instantiate (previouslyLoadedChips[componentName], pos, Quaternion.identity);
            loadedChipData.componentChips[i] = loadedComponentChip;

            // Load input pin names
            for (int inputIndex = 0; inputIndex < componentToLoad.inputPins.Length && inputIndex < loadedChipData.componentChips[i].inputPins.Length; inputIndex++) {
                loadedChipData.componentChips[i].inputPins[inputIndex].pinName = componentToLoad.inputPins[inputIndex].name;
                loadedChipData.componentChips[i].inputPins[inputIndex].wireType = componentToLoad.inputPins[inputIndex].wireType;
            }

            // Load output pin names
            for (int ouputIndex = 0; ouputIndex < componentToLoad.outputPins.Length; ouputIndex++) {
                loadedChipData.componentChips[i].outputPins[ouputIndex].pinName = componentToLoad.outputPins[ouputIndex].name;
                loadedChipData.componentChips[i].outputPins[ouputIndex].wireType = componentToLoad.outputPins[ouputIndex].wireType;
            }
        }

        // Connect pins with wires
        for (int chipIndex = 0; chipIndex < chipToLoad.savedComponentChips.Length; chipIndex++) {
            Chip loadedComponentChip = loadedChipData.componentChips[chipIndex];
            for (int inputPinIndex = 0; inputPinIndex < loadedComponentChip.inputPins.Length && inputPinIndex < chipToLoad.savedComponentChips[chipIndex].inputPins.Length; inputPinIndex++) {
                SavedInputPin savedPin = chipToLoad.savedComponentChips[chipIndex].inputPins[inputPinIndex];
                Pin pin = loadedComponentChip.inputPins[inputPinIndex];

                // If this pin should receive input from somewhere, then wire it up to that pin
                if (savedPin.parentChipIndex != -1) {
                    Pin connectedPin = loadedChipData.componentChips[savedPin.parentChipIndex].outputPins[savedPin.parentChipOutputIndex];
                    pin.cyclic = savedPin.isCylic;
                    Pin.TryConnect (connectedPin, pin);
                }
            }
        }

        return loadedChipData;
    }

    static ChipSaveData LoadChipWithWires (SavedChip chipToLoad, Dictionary<string, Chip> previouslyLoadedChips, Wire wirePrefab, ChipEditor chipEditor) {
        ChipSaveData loadedChipData = new ChipSaveData ();
        int numComponents = chipToLoad.savedComponentChips.Length;
        loadedChipData.componentChips = new Chip[numComponents];
        loadedChipData.chipName = chipToLoad.name;
        loadedChipData.chipColour = chipToLoad.colour;
        loadedChipData.chipNameColour = chipToLoad.nameColour;
        loadedChipData.creationIndex = chipToLoad.creationIndex;
        loadedChipData.folderName = chipToLoad.folderName;
        loadedChipData.scale = chipToLoad.scale;
        List<Wire> wiresToLoad = new List<Wire>();

        // Spawn component chips (the chips used to create this chip)
        // These will have been loaded already, and stored in the previouslyLoadedChips dictionary
        for (int i = 0; i < numComponents; i++) {
            SavedComponentChip componentToLoad = chipToLoad.savedComponentChips[i];
            string componentName = componentToLoad.chipName;
            Vector2 pos = new Vector2 ((float) componentToLoad.posX, (float) componentToLoad.posY);

            if (!previouslyLoadedChips.ContainsKey (componentName)) {
                DLSLogger.LogError ("Failed to load sub component: " + componentName + " While loading " + chipToLoad.name);
            }

            Chip loadedComponentChip = GameObject.Instantiate (previouslyLoadedChips[componentName], pos, Quaternion.identity, chipEditor.chipImplementationHolder);
            loadedComponentChip.gameObject.SetActive(true);
            loadedChipData.componentChips[i] = loadedComponentChip;

            // Load input pin names
            for (int inputIndex = 0; inputIndex < componentToLoad.inputPins.Length && inputIndex < loadedChipData.componentChips[i].inputPins.Length; inputIndex++) {
                loadedChipData.componentChips[i].inputPins[inputIndex].pinName = componentToLoad.inputPins[inputIndex].name;
            }

            // Load output pin names
            for (int ouputIndex = 0; ouputIndex < componentToLoad.outputPins.Length && ouputIndex < loadedChipData.componentChips[i].outputPins.Length; ouputIndex++) {
                loadedChipData.componentChips[i].outputPins[ouputIndex].pinName = componentToLoad.outputPins[ouputIndex].name;
            }
        }

        // Connect pins with wires
        for (int chipIndex = 0; chipIndex < chipToLoad.savedComponentChips.Length; chipIndex++) {
            Chip loadedComponentChip = loadedChipData.componentChips[chipIndex];
            for (int inputPinIndex = 0; inputPinIndex < loadedComponentChip.inputPins.Length && inputPinIndex < chipToLoad.savedComponentChips[chipIndex].inputPins.Length; inputPinIndex++) {
                SavedInputPin savedPin = chipToLoad.savedComponentChips[chipIndex].inputPins[inputPinIndex];
                Pin pin = loadedComponentChip.inputPins[inputPinIndex];

                // If this pin should receive input from somewhere, then wire it up to that pin
                if (savedPin.parentChipIndex != -1) {
                    Pin connectedPin = loadedChipData.componentChips[savedPin.parentChipIndex].outputPins[savedPin.parentChipOutputIndex];
                    pin.cyclic = savedPin.isCylic;
                    if (Pin.TryConnect (connectedPin, pin)) {
                        Wire loadedWire = GameObject.Instantiate (wirePrefab, chipEditor.wireHolder);
                        loadedWire.Connect (connectedPin, pin);
                        wiresToLoad.Add(loadedWire);
                    }
                }
            }
        }

        loadedChipData.wires = wiresToLoad.ToArray();

        return loadedChipData;
    }

    public static SavedWireLayout LoadWiringFile (string path) {
        using (StreamReader reader = new StreamReader (path)) {
            string wiringSaveString = reader.ReadToEnd ();
            return JsonUtility.FromJson<SavedWireLayout> (wiringSaveString);
        }
    }

    static void SortChipsByOrderOfCreation (ref SavedChip[] chips) {
        var sortedChips = new List<SavedChip> (chips);
        sortedChips.Sort ((a, b) => a.creationIndex.CompareTo (b.creationIndex));
        chips = sortedChips.ToArray ();
    }

    public static ChipSaveData GetChipSaveData(Chip chip, Chip[] builtinChips, List<Chip> spawnableChips, Wire wirePrefab, ChipEditor chipEditor) {
        // @NOTE: chipEditor can be removed here if:
        //     * Chip & wire instatiation is inside their respective implementation holders is inside the chipEditor
        //     * the wire connections are done inside ChipEditor.LoadFromSaveData instead of ChipLoader.LoadChipWithWires

        SavedChip chipToTryLoad;
        SavedChip[] savedChips = SaveSystem.GetAllSavedChips();

        using (StreamReader reader = new StreamReader(SaveSystem.GetPathToSaveFile(chip.name)))
        {
            string chipSaveString = reader.ReadToEnd();
            chipToTryLoad = JsonUtility.FromJson<SavedChip>(chipSaveString);
        }

        if (chipToTryLoad == null)
            return null;

        SortChipsByOrderOfCreation (ref savedChips);
        // Maintain dictionary of loaded chips (initially just the built-in chips)
        Dictionary<string, Chip> loadedChips = new Dictionary<string, Chip> ();
        for (int i = 0; i < builtinChips.Length; i++) {
            Chip builtinChip = builtinChips[i];
            loadedChips.Add (builtinChip.chipName, builtinChip);
        }
        foreach (Chip loadedChip in spawnableChips) {
            if (loadedChips.ContainsKey(loadedChip.chipName)) continue;
            loadedChips.Add (loadedChip.chipName, loadedChip);
        }

        ChipSaveData loadedChipData = LoadChipWithWires (chipToTryLoad, loadedChips, wirePrefab, chipEditor);
        SavedWireLayout wireLayout = LoadWiringFile(SaveSystem.GetPathToWireSaveFile(loadedChipData.chipName));

        // Set wires anchor points
        for (int i = 0; i < wireLayout.serializableWires.Length; i++) {
            string startPinName;
            string endPinName;

            SavedWire wire = wireLayout.serializableWires[i];
            // This fixes a bug which caused chips to be unable to be viewed/edited if some of input/output pins were swaped.
            try {
                startPinName = loadedChipData.componentChips[wire.parentChipIndex].outputPins[wire.parentChipOutputIndex].pinName;
                endPinName = loadedChipData.componentChips[wire.childChipIndex].inputPins[wire.childChipInputIndex].pinName;
            } catch (IndexOutOfRangeException) {
                // Swap input pins with output pins.
                startPinName = loadedChipData.componentChips[wire.parentChipIndex].inputPins[wire.parentChipOutputIndex].pinName;
                endPinName = loadedChipData.componentChips[wire.childChipIndex].outputPins[wire.childChipInputIndex].pinName;
            }
            //

            int wireIndex = Array.FindIndex(loadedChipData.wires, w => w.startPin.pinName == startPinName && w.endPin.pinName == endPinName);
            if (wireIndex >= 0) {
                loadedChipData.wires[wireIndex].SetAnchorPoints(wireLayout.serializableWires[i].anchorPoints);
            }
        }

        return loadedChipData;
    }

    public static void Import(string path) {
        SavedChip[] allChips = SaveSystem.GetAllSavedChips();
        List<string> newChipsPath = new List<string>();
        Dictionary<string, string> nameUpdateLookupTable = new Dictionary<string, string>();

        using (StreamReader reader = new StreamReader(path)) {
            int numberOfChips = Int32.Parse(reader.ReadLine());

            for (int i = 0; i < numberOfChips; i++) {
                string chipName = reader.ReadLine();
                int saveDataLength = Int32.Parse(reader.ReadLine());
                int wireSaveDataLength = Int32.Parse(reader.ReadLine());

                string saveData = "";
                string wireSaveData = "";

                for (int j = 0; j < saveDataLength; j++) {
                    saveData += reader.ReadLine() + "\n";
                }
                for (int j = 0; j < wireSaveDataLength; j++) {
                    wireSaveData += reader.ReadLine() + "\n";
                }

                // Rename chip if already exist
                if(Array.FindIndex(allChips, c => c.name == chipName) >= 0) {
                    int nameCounter = 2;
                    string newName;
                    do {
                        newName = chipName + nameCounter.ToString();
                        nameCounter++;
                    } while(Array.FindIndex(allChips, c => c.name == newName) >= 0);

                    nameUpdateLookupTable.Add(chipName, newName);
                    chipName = newName;
                }

                // Update name inside file if there was some names changed
                foreach (KeyValuePair<string, string> nameToReplace in nameUpdateLookupTable) {
                    saveData = saveData.Replace(
                                   "\"name\": \"" + nameToReplace.Key + "\"",
                                   "\"name\": \"" + nameToReplace.Value + "\""
                               ).Replace(
                                   "\"chipName\": \"" + nameToReplace.Key + "\"",
                                   "\"chipName\": \"" + nameToReplace.Value + "\""
                               );
                }

                string chipSaveFile = SaveSystem.GetPathToSaveFile(chipName);
                string chipWireSaveFile = SaveSystem.GetPathToWireSaveFile(chipName);
                newChipsPath.Add(chipSaveFile);

                using (StreamWriter writer = new StreamWriter(chipSaveFile))
                {
                    writer.Write(saveData);
                }

                using (StreamWriter writer = new StreamWriter(chipWireSaveFile))
                {
                    writer.Write(wireSaveData);
                }
            }
        }
    }
}
