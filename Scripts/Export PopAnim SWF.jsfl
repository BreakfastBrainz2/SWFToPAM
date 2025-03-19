/*
	<code> from https://github.com/SpringRoll/FlashToolkit/blob/master/src/JSFLLibraries/JSON.jsfl
*/
(function(global){
	
	/**
	*  The JSON serialization and unserialization methods
	*  @class JSON
	*/
	var JSON = {};

	JSON.prettyPrint = true//false;

	/**
	*  implement JSON.stringify serialization
	*  @method stringify
	*  @param {Object} obj The object to convert
	*/
	JSON.stringify = function(obj)
	{
		return _internalStringify(obj, 0);
	};

	function _internalStringify(obj, depth, fromArray)
	{
		var t = typeof (obj);
		if (t != "object" || obj === null)
		{
			// simple data type
			if (t == "string") return '"'+obj+'"';
			return String(obj);
		}
		else
		{
			// recurse array or object
			var n, v, json = [], arr = (obj && obj.constructor == Array);

			var joinString, bracketString, firstPropString;
			
			if(JSON.prettyPrint)
			{
				joinString = ",\n";
				bracketString = "\n";
				for(var i = 0; i < depth; ++i)
				{
					joinString += "\t";
					bracketString += "\t";
				}
				joinString += "\t";//one extra for the properties of this object
				firstPropString = bracketString + "\t";
			}
			else
			{
				joinString = ",";
				firstPropString = bracketString = "";
			}
			for (n in obj)
			{
				v = obj[n]; t = typeof(v);
				// Ignore functions
				if (t == "function") continue;

				if (t == "string") v = '"'+v+'"';
				else if (t == "object" && v !== null) v = _internalStringify(v, depth + 1, arr);

				json.push((arr ? "" : '"' + n + '":') + String(v));
			}
			if(json.length == 0)
			{
				return arr ? "[]" : "{}" 
			}
			else
			{
				return (fromArray || depth === 0 ? "" : bracketString)+ (arr ? "[" : "{") + firstPropString + json.join(joinString) + bracketString + (arr ? "]" : "}");
			}
		}
	}

	/**
	*  Implement JSON.parse de-serialization
	*  @method parse
	*  @param {String} str The string to de-serialize
	*/
	JSON.parse = function(str)
	{
		if (str === "") str = '""';
		eval("var p=" + str + ";"); // jshint ignore:line
		return p;
	};

	// Assign to global space
	global.JSON = JSON;

}(window));
/*
	</code>
*/

var dom = fl.getDocumentDOM();
var library = dom.library;
var timeline = dom.getTimeline();

var documentPath = dom.path;

var symbolActions = {};

function writeStringToFile(filePath, content) {
    var success = FLfile.write(toURI(filePath), content);
    if (success) {
        fl.trace("Successfully wrote to file: " + filePath);
    } else {
        fl.trace("Failed to write to file: " + filePath);
    }
}

function toURI(filePath) {
    filePath = filePath.replace(/\\/g, "/");

    if (filePath.indexOf("file:///") !== 0) {
        filePath = "file:///" + filePath;
    }

    filePath = encodeURI(filePath);

    return filePath;
}

function exportDocumentAsSWF() {
    var dom = fl.getDocumentDOM();

    var documentPath = dom.path;

    var swfFilePath = documentPath.replace(/\.fla$/i, ".swf");

    swfFilePath = toURI(swfFilePath);

    dom.exportSWF(swfFilePath, true);

    fl.trace("Exported SWF to: " + swfFilePath);
}

function trimString(str) {
    return str.replace(/^\s+|\s+$/g, "");
}

function escapeQuotes(str) {
    return str.replace(/"/g, '\\"');
}

function addFrameActions(symbolName, frame, layerName, frameNum) {
    if (frame.actionScript && frame.actionScript.length > 0) {
        if (!symbolActions[symbolName]) {
            symbolActions[symbolName] = {};
        }

        if (!symbolActions[symbolName][frameNum]) {
            symbolActions[symbolName][frameNum] = [];
        }

        var actions = frame.actionScript.split("\n");
        for (var i = 0; i < actions.length; i++) {
            var action = trimString(actions[i]);
            if (action.length > 0) {
                action = escapeQuotes(action);
                    symbolActions[symbolName][frameNum].push({
                        layer: layerName,
                        action: action
                    });
            }
        }
    }
}

function logSymbolActions(symbol) {
    if (symbol.timeline) {
        var layers = symbol.timeline.layers;
        for (var j = 0; j < layers.length; j++) {
            var layer = layers[j];
            if (layer.layerType === "normal" && layer.frames.length > 0) {
                var lastProcessedFrame = -1;
                for (var k = 0; k < layer.frames.length; k++) {
                    var frame = layer.frames[k];
                    if (frame.startFrame !== lastProcessedFrame) {
                        addFrameActions(symbol.name.split("/").pop(), frame, layer.name, frame.startFrame);
                        lastProcessedFrame = frame.startFrame;
                    }
                }
            }
        }
    }
}

function scriptMain()
{
	fl.outputPanel.clear();
	
	if (!documentPath) {
	fl.trace("Error: The document must be saved before running this script.");
	return;
    }

    for (var i = 0; i < library.items.length; i++) {
        var item = library.items[i];

        if (item.itemType === "movie clip") {
            var symbolName = item.name.split("/").pop();

            if (symbolName.charAt(0) !== "_") {
                item.linkageExportForAS = true;

                item.linkageClassName = symbolName;
                item.linkageExportInFirstFrame = true;
            }

            logSymbolActions(item);
        }

        else if (item.itemType === "bitmap") {
            item.compressionType = "lossless";
        }
    }


    // hacky - there's no way to obtain the root timeline (for some reason)
    // so do this to ensure the user is at the root timeline before gathering main actions.
    for (var z = 0; z < 20; z++) {
        fl.getDocumentDOM().exitEditMode();
    }

    var layers = fl.getDocumentDOM().getTimeline().layers;
    for (var j = 0; j < layers.length; j++) {
        var layer = layers[j];
        if (layer.layerType === "normal" && layer.frames.length > 0) {
            var lastProcessedFrame = -1;
            for (var k = 0; k < layer.frames.length; k++) {
                var frame = layer.frames[k];
                if (frame.startFrame !== lastProcessedFrame) {
                    addFrameActions("main", frame, layer.name, frame.startFrame);
                    lastProcessedFrame = frame.startFrame;
                }
            }
        }
    }

    exportDocumentAsSWF();
    
    var actionsFilePath;
    if (/\.fla$/i.test(documentPath)) 
    {
        actionsFilePath = documentPath.replace(/\.fla$/i, "_actions.json");
    } else if (/\.xfl$/i.test(documentPath)) 
    {
        actionsFilePath = documentPath.replace(/\.xfl$/i, "_actions.json");
    }
    var jsonOutput = JSON.stringify(symbolActions);
    writeStringToFile(actionsFilePath, jsonOutput);
    fl.trace("PAM export finished.");
}

scriptMain();