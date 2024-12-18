module Expecto

open Expecto

module Main =

    [<EntryPoint>]
    let main args =
        // Run tests sequentially given the importance of timings
        runTestsInAssemblyWithCLIArgs [ CLIArguments.Sequenced ] args

