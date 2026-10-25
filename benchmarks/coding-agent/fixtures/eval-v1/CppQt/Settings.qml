import QtQuick
import QtQuick.Controls

Item {
    width: 320
    height: 480
    Column {
        Repeater {
            model: 100
            delegate: Button { text: "Item " + index }
        }
    }
}
