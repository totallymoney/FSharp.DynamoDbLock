module Expecto

open Expecto
open Serilog

module Main =

    Log.Logger <-
        LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console() // Can be useful for debugging
            .CreateLogger()

    [<EntryPoint>]
    let main args =
        runTestsInAssemblyWithCLIArgs [] args

