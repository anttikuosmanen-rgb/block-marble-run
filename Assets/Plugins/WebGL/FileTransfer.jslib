// Handing a creation to the player as a file (DESIGN.md §8.2).
//
// A build's saves live in IndexedDB, which the browser scopes to the origin - so a creation made on
// one port cannot be seen from another, and nothing made in a browser can be reached from the editor
// at all. A file is the way across: the same JSON the save store holds, written to disk where the
// player, and the level-bundling tool, can pick it up.
//
// A download needs an anchor click, which browsers only honour from a user gesture. Every call here
// arrives on one - the player pressed a button - so this is safe; a download started from a timer or
// on load would be blocked, and correctly so.

var BlockMarbleRunFileTransfer = {

  BMR_Download: function (namePtr, jsonPtr) {
    var name = UTF8ToString(namePtr);
    var json = UTF8ToString(jsonPtr);

    try {
      var blob = new Blob([json], { type: 'application/json' });
      var url = URL.createObjectURL(blob);

      var link = document.createElement('a');
      link.href = url;
      link.download = name;

      // Attached to the document before clicking: Firefox ignores a click on an anchor that is not
      // in the tree, and does so silently.
      document.body.appendChild(link);
      link.click();
      document.body.removeChild(link);

      // Not revoked immediately - Safari has not finished reading the blob when click() returns, and
      // revoking under it produces a download of zero bytes.
      setTimeout(function () { URL.revokeObjectURL(url); }, 10000);

      return 1;
    } catch (e) {
      console.error('[BMR] download failed', e);
      return 0;
    }
  },
};

mergeInto(LibraryManager.library, BlockMarbleRunFileTransfer);
