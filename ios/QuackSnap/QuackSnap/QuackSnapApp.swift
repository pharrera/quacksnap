import SwiftUI

@main
struct QuackSnapApp: App {
    @State private var model = AppModel()
    @Environment(\.scenePhase) private var scenePhase

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environment(model)
        }
        .onChange(of: scenePhase) { _, phase in
            switch phase {
            case .active:
                model.startReceiving()
            case .background:
                // iOS will tear the listener down anyway once we're suspended;
                // do it deliberately so state stays truthful.
                model.stopReceiving()
            default:
                break
            }
        }
    }
}
