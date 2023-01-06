namespace FsAutoComplete

open System
open System.IO
open Fantomas.Client.Contracts
open Fantomas.Client.LSPFantomasService
open FsAutoComplete.Logging
open FsAutoComplete.UnionPatternMatchCaseGenerator
open FsAutoComplete.RecordStubGenerator
open System.Threading
open Utils
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.EditorServices
open FSharp.Compiler.Text
open Ionide.ProjInfo
open Ionide.ProjInfo.ProjectSystem
open FsToolkit.ErrorHandling
open FSharp.Analyzers
open FSharp.UMX
open FSharp.Compiler.Tokenization
open SymbolLocation
open FSharp.Compiler.Symbols
open System.Collections.Immutable
open System.Collections.Generic
open Ionide.ProjInfo.ProjectSystem


[<RequireQualifiedAccess>]
type LocationResponse<'a> = Use of 'a

[<RequireQualifiedAccess>]
type HelpText =
  | Simple of symbol: string * text: string
  | Full of symbol: string * tip: ToolTipText * textEdits: CompletionNamespaceInsert option

[<RequireQualifiedAccess>]
type CoreResponse<'a> =
  | InfoRes of text: string
  | ErrorRes of text: string
  | Res of 'a

[<RequireQualifiedAccess>]
type FormatDocumentResponse =
  | Formatted of source: NamedText * formatted: string
  | FormattedRange of source: NamedText * formatted: string * range: FormatSelectionRange
  | UnChanged
  | Ignored
  | ToolNotPresent
  | Error of string

/// Represents a desired change to a given file
type DocumentEdit =
  { InsertPosition: pos
    InsertText: string }

module private Result =
  let ofCoreResponse (r: CoreResponse<'a>) =
    match r with
    | CoreResponse.Res a -> Ok a
    | CoreResponse.ErrorRes msg
    | CoreResponse.InfoRes msg -> Error msg

module AsyncResult =

  let inline mapErrorRes ar : Async<CoreResponse<'a>> =
    AsyncResult.foldResult id CoreResponse.ErrorRes ar

  let recoverCancellationGeneric (ar: Async<Result<'t, exn>>) recoverInternal =
    AsyncResult.foldResult id recoverInternal ar

  let recoverCancellation (ar: Async<Result<CoreResponse<'t>, exn>>) =
    recoverCancellationGeneric ar (sprintf "Request cancelled (exn was %A)" >> CoreResponse.InfoRes)

  let recoverCancellationIgnore (ar: Async<Result<unit, exn>>) = AsyncResult.foldResult id ignore ar

[<RequireQualifiedAccess>]
type NotificationEvent =
  | ParseError of errors: FSharpDiagnostic[] * file: string<LocalPath>
  | Workspace of ProjectSystem.ProjectResponse
  | AnalyzerMessage of messages: FSharp.Analyzers.SDK.Message[] * file: string<LocalPath>
  | UnusedOpens of file: string<LocalPath> * opens: Range[]
  // | Lint of file: string<LocalPath> * warningsWithCodes: Lint.EnrichedLintWarning list
  | UnusedDeclarations of file: string<LocalPath> * decls: range[]
  | SimplifyNames of file: string<LocalPath> * names: SimplifyNames.SimplifiableRange[]
  | Canceled of errorMessage: string
  | FileParsed of string<LocalPath>
  | TestDetected of file: string<LocalPath> * tests: TestAdapter.TestAdapterEntry<range>[]

module Commands =
  let fantomasLogger = LogProvider.getLoggerByName "Fantomas"
  let commandsLogger = LogProvider.getLoggerByName "Commands"

  let addFile (fsprojPath: string) fileVirtPath =
    async {
      try
        let dir = Path.GetDirectoryName fsprojPath
        let newFilePath = Path.Combine(dir, fileVirtPath)
        let fileInfo = FileInfo(newFilePath)

        // Ensure the destination directory exist
        if not fileInfo.Directory.Exists then
          fileInfo.Directory.Create()

        (File.Open(newFilePath, FileMode.OpenOrCreate)).Close()

        FsProjEditor.addFile fsprojPath fileVirtPath
        return CoreResponse.Res()
      with ex ->
        return CoreResponse.ErrorRes ex.Message
    }

  let addFileAbove (fsprojPath: string) (fileVirtPath: string) newFileName =
    async {
      try
        let dir = Path.GetDirectoryName fsprojPath
        let virtPathDir = Path.GetDirectoryName fileVirtPath

        let newFilePath = Path.Combine(dir, virtPathDir, newFileName)
        let fileInfo = FileInfo(newFilePath)

        // Ensure the destination directory exist
        if not fileInfo.Directory.Exists then
          fileInfo.Directory.Create()

        (File.Open(newFilePath, FileMode.OpenOrCreate)).Close()

        let newVirtPath = Path.Combine(virtPathDir, newFileName)
        FsProjEditor.addFileAbove fsprojPath fileVirtPath newVirtPath
        return CoreResponse.Res()
      with ex ->
        return CoreResponse.ErrorRes ex.Message
    }

  let addFileBelow (fsprojPath: string) (fileVirtPath: string) newFileName =
    async {
      try
        let dir = Path.GetDirectoryName fsprojPath
        let virtPathDir = Path.GetDirectoryName fileVirtPath

        let newFilePath = Path.Combine(dir, virtPathDir, newFileName)
        let fileInfo = FileInfo(newFilePath)

        // Ensure the destination directory exist
        if not fileInfo.Directory.Exists then
          fileInfo.Directory.Create()

        (File.Open(newFilePath, FileMode.OpenOrCreate)).Close()

        let newVirtPath = Path.Combine(virtPathDir, newFileName)
        FsProjEditor.addFileBelow fsprojPath fileVirtPath newVirtPath
        return CoreResponse.Res()
      with ex ->
        return CoreResponse.ErrorRes ex.Message
    }

  let removeFile fsprojPath fileVirtPath =
    async {
      FsProjEditor.removeFile fsprojPath fileVirtPath
      return CoreResponse.Res()
    }

  let addExistingFile fsprojPath fileVirtPath =
    async {
      FsProjEditor.addExistingFile fsprojPath fileVirtPath
      return CoreResponse.Res()
    }

  let getRangesAtPosition (getParseResultsForFile: _ -> Async<Result<FSharpParseFileResults, _>>) file positions =
    asyncResult {
      let! ast = getParseResultsForFile file
      return positions |> List.map (UntypedAstUtils.getRangesAtPosition ast.ParseTree)
    }

  let scopesForFile
    (getParseResultsForFile: _ -> Async<Result<NamedText * FSharpParseFileResults, _>>)
    (file: string<LocalPath>)
    =
    asyncResult {

      let! (text, ast) = getParseResultsForFile file

      let ranges =
        Structure.getOutliningRanges (text.ToString().Split("\n")) ast.ParseTree

      return ranges
    }

  let docForText (lines: NamedText) (tyRes: ParseAndCheckResults) : Document =
    { LineCount = lines.Lines.Length
      FullName = tyRes.FileName // from the compiler, assumed safe
      GetText = fun _ -> string lines
      GetLineText0 = fun i -> (lines :> ISourceText).GetLineString i
      GetLineText1 = fun i -> (lines :> ISourceText).GetLineString(i - 1) }

  let getAbstractClassStub
    tryFindAbstractClassExprInBufferAtPos
    writeAbstractClassStub
    (tyRes: ParseAndCheckResults)
    (objExprRange: Range)
    (lines: NamedText)
    (lineStr: LineStr)
    =
    asyncResult {
      let doc = docForText lines tyRes

      let! abstractClass =
        tryFindAbstractClassExprInBufferAtPos objExprRange.Start doc
        |> Async.map (Result.ofOption (fun _ -> CoreResponse.InfoRes "Abstract class at position not found"))

      let! (insertPosition, generatedCode) =
        writeAbstractClassStub tyRes doc lines lineStr abstractClass
        |> Async.map (Result.ofOption (fun _ -> CoreResponse.InfoRes "Didn't need to write an abstract class"))

      return CoreResponse.Res(generatedCode, insertPosition)
    }

  let getRecordStub
    tryFindRecordDefinitionFromPos
    (tyRes: ParseAndCheckResults)
    (pos: Position)
    (lines: NamedText)
    (line: LineStr)
    =
    async {
      let doc = docForText lines tyRes
      let! res = tryFindRecordDefinitionFromPos pos doc

      match res with
      | None -> return CoreResponse.InfoRes "Record at position not found"
      | Some (recordEpr, (Some recordDefinition), insertionPos) ->
        if shouldGenerateRecordStub recordEpr recordDefinition then
          let result = formatRecord insertionPos "$1" recordDefinition recordEpr.FieldExprList
          return CoreResponse.Res(result, insertionPos.InsertionPos)
        else
          return CoreResponse.InfoRes "Record at position not found"
      | _ -> return CoreResponse.InfoRes "Record at position not found"
    }

  let getNamespaceSuggestions (tyRes: ParseAndCheckResults) (pos: Position) (line: LineStr) =
    async {
      match Lexer.findLongIdents (pos.Column, line) with
      | None -> return CoreResponse.InfoRes "Ident not found"
      | Some (_, idents) ->
        match ParsedInput.GetEntityKind(pos, tyRes.GetParseResults.ParseTree) with
        | None -> return CoreResponse.InfoRes "EntityKind not found"
        | Some entityKind ->

          let symbol = Lexer.getSymbol pos.Line pos.Column line SymbolLookupKind.Fuzzy [||]

          match symbol with
          | None -> return CoreResponse.InfoRes "Symbol at position not found"
          | Some sym ->


            let entities = tyRes.GetAllEntities true

            let isAttribute = entityKind = EntityKind.Attribute

            let entities =
              entities
              |> List.filter (fun e ->
                match entityKind, (e.Kind LookupType.Fuzzy) with
                | EntityKind.Attribute, EntityKind.Attribute
                | EntityKind.Type, (EntityKind.Type | EntityKind.Attribute)
                | EntityKind.FunctionOrValue _, _ -> true
                | EntityKind.Attribute, _
                | _, EntityKind.Module _
                | EntityKind.Module _, _
                | EntityKind.Type, _ -> false)

            let maybeUnresolvedIdents =
              idents |> Array.map (fun ident -> { Ident = ident; Resolved = false })

            let entities =
              entities
              |> List.collect (fun e ->
                [ yield e.TopRequireQualifiedAccessParent, e.AutoOpenParent, e.Namespace, e.CleanedIdents
                  if isAttribute then
                    let lastIdent = e.CleanedIdents.[e.CleanedIdents.Length - 1]

                    if
                      (e.Kind LookupType.Fuzzy) = EntityKind.Attribute
                      && lastIdent.EndsWith "Attribute"
                    then
                      yield
                        e.TopRequireQualifiedAccessParent,
                        e.AutoOpenParent,
                        e.Namespace,
                        e.CleanedIdents
                        |> Array.replace (e.CleanedIdents.Length - 1) (lastIdent.Substring(0, lastIdent.Length - 9)) ])

            let createEntity =
              ParsedInput.TryFindInsertionContext
                pos.Line
                tyRes.GetParseResults.ParseTree
                maybeUnresolvedIdents
                OpenStatementInsertionPoint.Nearest

            let word = sym.Text

            let candidates = entities |> Seq.collect createEntity |> Seq.toList

            let openNamespace =
              candidates
              |> List.choose (fun (entity, ctx) ->
                entity.Namespace |> Option.map (fun ns -> ns, entity.FullDisplayName, ctx))
              |> List.groupBy (fun (ns, _, _) -> ns)
              |> List.map (fun (ns, xs) ->
                ns,
                xs
                |> List.map (fun (_, name, ctx) -> name, ctx)
                |> List.distinctBy (fun (name, _) -> name)
                |> List.sortBy fst)
              |> List.collect (fun (ns, names) ->
                let multipleNames =
                  match names with
                  | [] -> false
                  | [ _ ] -> false
                  | _ -> true

                names |> List.map (fun (name, ctx) -> ns, name, ctx, multipleNames))

            let qualifySymbolActions =
              candidates
              |> List.map (fun (entity, _) -> entity.FullRelativeName, entity.Qualifier)
              |> List.distinct
              |> List.sort

            return CoreResponse.Res(word, openNamespace, qualifySymbolActions)
    }

  let getUnionPatternMatchCases
    tryFindUnionDefinitionFromPos
    (tyRes: ParseAndCheckResults)
    (pos: Position)
    (lines: ISourceText)
    (line: LineStr)
    =
    async {

      let doc =
        { Document.LineCount = lines.Length
          FullName = tyRes.FileName
          GetText = fun _ -> string lines
          GetLineText0 = fun i -> lines.GetLineString i
          GetLineText1 = fun i -> lines.GetLineString(i - 1) }

      let! res = tryFindUnionDefinitionFromPos pos doc

      match res with
      | None -> return CoreResponse.InfoRes "Union at position not found"
      | Some (patMatchExpr, unionTypeDefinition, insertionPos) ->

        if shouldGenerateUnionPatternMatchCases patMatchExpr unionTypeDefinition then
          let result = formatMatchExpr insertionPos "$1" patMatchExpr unionTypeDefinition

          return CoreResponse.Res(result, insertionPos.InsertionPos)
        else
          return CoreResponse.InfoRes "Union at position not found"
    }

  let formatSelection
    (tryGetFileCheckerOptionsWithLines: _ -> Result<NamedText, _>)
    (formatSelectionAsync: _ -> System.Threading.Tasks.Task<FantomasResponse>)
    (file: string<LocalPath>)
    (rangeToFormat: FormatSelectionRange)
    : Async<Result<FormatDocumentResponse, string>> =
    asyncResult {
      try
        let filePath = (UMX.untag file)
        let! text = tryGetFileCheckerOptionsWithLines file
        let currentCode = string text

        let! fantomasResponse =
          formatSelectionAsync
            { SourceCode = currentCode
              FilePath = filePath
              Config = None
              Range = rangeToFormat }

        match fantomasResponse with
        | { Code = 1
            Content = Some code
            SelectedRange = Some range } ->
          fantomasLogger.debug (Log.setMessage (sprintf "Fantomas daemon was able to format selection in \"%A\"" file))
          return FormatDocumentResponse.FormattedRange(text, code, range)
        | { Code = 2 } ->
          fantomasLogger.debug (Log.setMessage (sprintf "\"%A\" did not change after formatting" file))
          return FormatDocumentResponse.UnChanged
        | { Code = 3; Content = Some error } ->
          fantomasLogger.error (Log.setMessage (sprintf "Error while formatting \"%A\"\n%s" file error))
          return FormatDocumentResponse.Error(sprintf "Formatting failed!\n%A" fantomasResponse)
        | { Code = 4 } ->
          fantomasLogger.debug (Log.setMessage (sprintf "\"%A\" was listed in a .fantomasignore file" file))
          return FormatDocumentResponse.Ignored
        | { Code = 6 } -> return FormatDocumentResponse.ToolNotPresent
        | _ ->
          fantomasLogger.warn (
            Log.setMessage (
              sprintf
                "Fantomas daemon was unable to format \"%A\", due to unexpected result code %i\n%A"
                file
                fantomasResponse.Code
                fantomasResponse
            )
          )

          return FormatDocumentResponse.Error(sprintf "Formatting failed!\n%A" fantomasResponse)
      with ex ->
        fantomasLogger.warn (
          Log.setMessage "Errors while formatting file, defaulting to previous content. Error message was {message}"
          >> Log.addContextDestructured "message" ex.Message
          >> Log.addExn ex
        )

        return! Core.Error ex.Message
    }

  let formatDocument
    (tryGetFileCheckerOptionsWithLines: _ -> Result<NamedText, _>)
    (formatDocumentAsync: _ -> System.Threading.Tasks.Task<FantomasResponse>)
    (file: string<LocalPath>)
    : Async<Result<FormatDocumentResponse, string>> =
    asyncResult {
      try
        let filePath = (UMX.untag file)
        let! text = tryGetFileCheckerOptionsWithLines file
        let currentCode = string text

        let! fantomasResponse =
          formatDocumentAsync
            { SourceCode = currentCode
              FilePath = filePath
              Config = None }

        match fantomasResponse with
        | { Code = 1; Content = Some code } ->
          fantomasLogger.debug (Log.setMessage (sprintf "Fantomas daemon was able to format \"%A\"" file))
          return FormatDocumentResponse.Formatted(text, code)
        | { Code = 2 } ->
          fantomasLogger.debug (Log.setMessage (sprintf "\"%A\" did not change after formatting" file))
          return FormatDocumentResponse.UnChanged
        | { Code = 3; Content = Some error } ->
          fantomasLogger.error (Log.setMessage (sprintf "Error while formatting \"%A\"\n%s" file error))
          return FormatDocumentResponse.Error(sprintf "Formatting failed!\n%A" fantomasResponse)
        | { Code = 4 } ->
          fantomasLogger.debug (Log.setMessage (sprintf "\"%A\" was listed in a .fantomasignore file" file))
          return FormatDocumentResponse.Ignored
        | { Code = 6 } -> return FormatDocumentResponse.ToolNotPresent
        | _ ->
          fantomasLogger.warn (
            Log.setMessage (
              sprintf
                "Fantomas daemon was unable to format \"%A\", due to unexpected result code %i\n%A"
                file
                fantomasResponse.Code
                fantomasResponse
            )
          )

          return FormatDocumentResponse.Error(sprintf "Formatting failed!\n%A" fantomasResponse)
      with ex ->
        fantomasLogger.warn (
          Log.setMessage "Errors while formatting file, defaulting to previous content. Error message was {message}"
          >> Log.addContextDestructured "message" ex.Message
          >> Log.addExn ex
        )

        return! Core.Error ex.Message
    }

  let symbolImplementationProject
    getProjectOptions
    getUsesOfSymbol
    getAllProjects
    (tyRes: ParseAndCheckResults)
    (pos: Position)
    lineStr
    =

    let filterSymbols symbols =
      symbols
      |> Array.where (fun (su: FSharpSymbolUse) ->
        su.IsFromDispatchSlotImplementation
        || (su.IsFromType
            && not (tyRes.GetParseResults.IsTypeAnnotationGivenAtPosition(su.Range.Start))))

    async {
      match tyRes.TryGetSymbolUseAndUsages pos lineStr with
      | Ok (sym, usages) ->
        let fsym = sym.Symbol

        if fsym.IsPrivateToFile then
          return CoreResponse.Res(LocationResponse.Use(sym, filterSymbols usages))
        else if fsym.IsInternalToProject then
          let opts = getProjectOptions tyRes.FileName
          let! symbols = getUsesOfSymbol (tyRes.FileName, [ UMX.untag tyRes.FileName, opts ], sym.Symbol)
          return CoreResponse.Res(LocationResponse.Use(sym, filterSymbols symbols))
        else
          let! symbols = getUsesOfSymbol (tyRes.FileName, getAllProjects (), sym.Symbol)
          let symbols = filterSymbols symbols
          return CoreResponse.Res(LocationResponse.Use(sym, filterSymbols symbols))
      | Error e -> return CoreResponse.ErrorRes e
    }

  let renameSymbol
    (symbolUseWorkspace: _
                           -> _
                           -> _
                           -> _
                           -> Async<Result<Choice<Dictionary<string, range array> * Dictionary<string, range array>, Dictionary<string, range array>>, string>>)
    (tryGetFileSource: _ -> Result<NamedText, _>)
    (pos: Position)
    (tyRes: ParseAndCheckResults)
    (lineStr: LineStr)
    (text: NamedText)
    =
    asyncResult {
      match! symbolUseWorkspace pos lineStr text tyRes with
      | Choice1Of2 (declarationsByDocument, symbolUsesByDocument) ->
        let totalSetOfRanges = Dictionary<NamedText, _>()

        for (KeyValue (filePath, declUsages)) in declarationsByDocument do
          let! text = tryGetFileSource (UMX.tag filePath)

          match totalSetOfRanges.TryGetValue(text) with
          | true, ranges -> totalSetOfRanges[text] <- Array.append ranges declUsages
          | false, _ -> totalSetOfRanges[text] <- declUsages

        for (KeyValue (filePath, symbolUses)) in symbolUsesByDocument do
          let! text = tryGetFileSource (UMX.tag filePath)

          match totalSetOfRanges.TryGetValue(text) with
          | true, ranges -> totalSetOfRanges[text] <- Array.append ranges symbolUses
          | false, _ -> totalSetOfRanges[text] <- symbolUses

        return totalSetOfRanges |> Seq.map (fun (KeyValue (k, v)) -> k, v) |> Array.ofSeq
      | Choice2Of2 (mixedDeclarationAndSymbolUsesByDocument) ->
        let totalSetOfRanges = Dictionary<NamedText, _>()

        for (KeyValue (filePath, symbolUses)) in mixedDeclarationAndSymbolUsesByDocument do
          let! text = tryGetFileSource (UMX.tag filePath)

          match totalSetOfRanges.TryGetValue(text) with
          | true, ranges -> totalSetOfRanges[text] <- Array.append ranges symbolUses
          | false, _ -> totalSetOfRanges[text] <- symbolUses

        return totalSetOfRanges |> Seq.map (fun (KeyValue (k, v)) -> k, v) |> Array.ofSeq
    }

  let typesig (tyRes: ParseAndCheckResults) (pos: Position) lineStr =
    tyRes.TryGetToolTip pos lineStr
    |> Result.bimap CoreResponse.Res CoreResponse.ErrorRes

  let pipelineHints (tryGetFileSource: _ -> Result<NamedText, _>) (tyRes: ParseAndCheckResults) =
    result {
      // Debug.waitForDebuggerAttached "AdaptiveServer"
      let! contents = tryGetFileSource tyRes.FileName

      let getSignatureAtPos pos =
        option {
          let! lineStr = contents.GetLine pos

          let! tip = tyRes.TryGetToolTip pos lineStr |> Option.ofResult

          return TipFormatter.extractGenericParameters tip
        }
        |> Option.defaultValue []

      let areTokensCommentOrWhitespace (tokens: FSharpTokenInfo list) =
        tokens
        |> List.exists (fun token ->
          token.CharClass <> FSharpTokenCharKind.Comment
          && token.CharClass <> FSharpTokenCharKind.WhiteSpace
          && token.CharClass <> FSharpTokenCharKind.LineComment)
        |> not

      let getStartingPipe =
        function
        | y :: xs when y.TokenName.ToUpper() = "INFIX_BAR_OP" -> Some y
        | x :: y :: xs when x.TokenName.ToUpper() = "WHITESPACE" && y.TokenName.ToUpper() = "INFIX_BAR_OP" -> Some y
        | _ -> None

      let folder (lastExpressionLine, lastExpressionLineWasPipe, acc) (currentIndex, currentTokens) =
        let isCommentOrWhitespace = areTokensCommentOrWhitespace currentTokens

        let isPipe = getStartingPipe currentTokens

        match isCommentOrWhitespace, isPipe with
        | true, _ -> lastExpressionLine, lastExpressionLineWasPipe, acc
        | false, Some pipe ->
          currentIndex, true, (lastExpressionLine, lastExpressionLineWasPipe, currentIndex, pipe) :: acc
        | false, None -> currentIndex, false, acc

      let hints =
        Array.init ((contents: ISourceText).GetLineCount()) (fun line -> (contents: ISourceText).GetLineString line)
        |> Array.map (Lexer.tokenizeLine [||])
        |> Array.mapi (fun currentIndex currentTokens -> currentIndex, currentTokens)
        |> Array.fold folder (0, false, [])
        |> (fun (_, _, third) -> third |> Array.ofList)
        |> Array.Parallel.map (fun (lastExpressionLine, lastExpressionLineWasPipe, currentIndex, pipeToken) ->
          let pipePos = Position.fromZ currentIndex pipeToken.RightColumn
          let gens = getSignatureAtPos pipePos

          let previousNonPipeLine =
            if lastExpressionLineWasPipe then
              None
            else
              Some lastExpressionLine

          currentIndex, previousNonPipeLine, gens)

      return CoreResponse.Res hints
    }
    |> Result.fold id (fun _ -> CoreResponse.InfoRes "Couldn't find file content")

  let calculateNamespaceInsert
    currentAst
    (decl: DeclarationListItem)
    (pos: Position)
    getLine
    : CompletionNamespaceInsert option =
    let getLine (p: Position) = getLine p |> Option.defaultValue ""

    let idents = decl.FullName.Split '.'

    decl.NamespaceToOpen
    |> Option.bind (fun n ->
      (currentAst ())
      |> Option.map (fun ast ->
        ParsedInput.FindNearestPointToInsertOpenDeclaration (pos.Line) ast idents OpenStatementInsertionPoint.Nearest)
      |> Option.map (fun ic ->
        //TODO: unite with `CodeFix/ResolveNamespace`
        //TODO: Handle Nearest AND TopLevel. Currently it's just Nearest (vs. ResolveNamespace -> TopLevel) (#789)

        let detectIndentation (line: string) =
          line |> Seq.takeWhile ((=) ' ') |> Seq.length

        // adjust line
        let pos =
          match ic.ScopeKind with
          | ScopeKind.Namespace ->
            // for namespace `open` isn't created close at namespace,
            // but instead on first member
            // -> move `open` closer to namespace
            // this only happens when there are no other `open`

            // from insert position go up until first open OR namespace
            ic.Pos.LinesToBeginning()
            |> Seq.tryFind (fun l ->
              let lineStr = getLine l
              // namespace MUST be top level -> no indentation
              lineStr.StartsWith "namespace ")
            |> function
              // move to the next line below "namespace"
              | Some l -> l.IncLine()
              | None -> ic.Pos
          | _ -> ic.Pos

        // adjust column
        let pos =
          match pos with
          | Pos (1, c) -> pos
          | Pos (l, 0) ->
            let prev = getLine (pos.DecLine())
            let indentation = detectIndentation prev

            if indentation <> 0 then
              // happens when there are already other `open`s
              Position.mkPos l indentation
            else
              pos
          | Pos (_, c) -> pos

        { Namespace = n
          Position = pos
          Scope = ic.ScopeKind }))

  let symbolUseWorkspace
    getDeclarationLocation
    (findReferencesForSymbolInFile: (string * FSharpProjectOptions * FSharpSymbol) -> Async<Range seq>)
    (tryGetFileSource: string<LocalPath> -> ResultOrString<NamedText>)
    getProjectOptionsForFsproj
    pos
    lineStr
    (text: NamedText)
    (tyRes: ParseAndCheckResults)
    =
    asyncResult {

      let findReferencesInFile
        (
          file,
          symbol: FSharpSymbol,
          project: FSharpProjectOptions,
          onFound: range -> Async<unit>
        ) =
        asyncResult {
          try
            let! (references: Range seq) = findReferencesForSymbolInFile (file, project, symbol)

            for reference in references do
              do! onFound reference
          with e ->
            commandsLogger.error (
              Log.setMessage "Failed findReferencesForSymbolInFile with {file}"
              >> Log.addExn e
              >> Log.addContextDestructured "file" file
            // >> Log.addContextDestructured "symbol" symbol
            )
        }

      let getSymbolUsesInProjects (symbol, projects: FSharpProjectOptions list, onFound) =
        projects
        |> List.map (fun p ->
          asyncResult {
            for file in p.SourceFiles do
              do! findReferencesInFile (file, symbol, p, onFound)
          })
        |> Async.Parallel
        |> Async.map (Array.toList >> FsToolkit.ErrorHandling.List.sequenceResultM)

      let ranges (uses: FSharpSymbolUse[]) = uses |> Array.map (fun u -> u.Range)

      let splitByDeclaration (uses: FSharpSymbolUse[]) =
        uses |> Array.partition (fun u -> u.IsFromDefinition)

      let toDict (symbolUseRanges: range[]) =
        let dict = new System.Collections.Generic.Dictionary<string, range[]>()

        symbolUseRanges
        |> Array.collect (fun symbolUse ->
          let file = symbolUse.FileName
          // if we had a more complex project system (one that understood that the same file could be in multiple projects distinctly)
          // then we'd need to map the files to some kind of document identfier and dedupe by that
          // before issueing the renames. We don't, so this becomes very simple
          [| file, symbolUse |])
        |> Array.groupBy fst
        |> Array.iter (fun (key, items) ->
          let itemsSeq = items |> Array.map snd
          dict[key] <- itemsSeq
          ())

        dict

      let! symUse =
        tyRes.TryGetSymbolUse pos lineStr
        |> Result.ofOption (fun _ -> "No result found")

      let symbol = symUse.Symbol

      let! declLoc =
        getDeclarationLocation (symUse, text)
        |> Result.ofOption (fun _ -> "No declaration location found")

      match declLoc with
      | SymbolDeclarationLocation.CurrentDocument ->
        let! ct = Async.CancellationToken
        let symbolUses = tyRes.GetCheckResults.GetUsesOfSymbolInFile(symbol, ct)
        let declarations, usages = splitByDeclaration symbolUses

        let declarationRanges, usageRanges =
          toDict (ranges declarations), toDict (ranges usages)

        return Choice1Of2(declarationRanges, usageRanges)

      | SymbolDeclarationLocation.Projects (projects, isInternalToProject) ->
        let symbolUseRanges = ImmutableArray.CreateBuilder()
        let symbolRange = symbol.DefinitionRange.NormalizeDriveLetterCasing()
        let symbolFile = symbolRange.TaggedFileName

        let symbolFileText =
          tryGetFileSource (symbolFile)
          |> Result.fold id (fun e -> failwith $"Unable to get file source for file '{symbolFile}'")

        let! symbolText = symbolFileText.[symbolRange]
        // |> Result.fold id (fun e -> failwith "Unable to get text for initial symbol use")

        let projects =
          if isInternalToProject then
            projects
          else
            [ for project in projects do
                yield project

                yield!
                  project.ReferencedProjects
                  |> Array.choose (fun p -> p.OutputFile |> getProjectOptionsForFsproj) ]
            |> List.distinctBy (fun x -> x.ProjectFileName)

        let onFound (symbolUseRange: range) =
          async {
            let symbolUseRange = symbolUseRange.NormalizeDriveLetterCasing()
            let symbolFile = symbolUseRange.TaggedFileName
            let targetText = tryGetFileSource (symbolFile)

            match targetText with
            | Error e -> ()
            | Ok sourceText ->
              let sourceSpan =
                sourceText.[symbolUseRange]
                |> Result.fold id (fun e -> failwith "Unable to get text for symbol use")

              // There are two kinds of ranges we get back:
              // * ranges that exactly match the short name of the symbol
              // * ranges that are longer than the short name of the symbol,
              //   typically because we're talking about some kind of fully-qualified usage
              // For the latter, we need to adjust the reported range to just be the portion
              // of the fully-qualfied text that is the symbol name.
              if sourceSpan = symbolText then
                symbolUseRanges.Add symbolUseRange
              else
                match sourceSpan.IndexOf(symbolText) with
                | -1 -> ()
                | n ->
                  if sourceSpan.Length >= n + symbolText.Length then
                    let startPos = symbolUseRange.Start.IncColumn n
                    let endPos = symbolUseRange.Start.IncColumn(n + symbolText.Length)

                    let actualUseRange = Range.mkRange symbolUseRange.FileName startPos endPos
                    symbolUseRanges.Add actualUseRange
          }

        let! _ = getSymbolUsesInProjects (symbol, projects, onFound)

        // Distinct these down because each TFM will produce a new 'project'.
        // Unless guarded by a #if define, symbols with the same range will be added N times
        let symbolUseRanges = symbolUseRanges.ToArray() |> Array.distinct

        return Choice2Of2(toDict symbolUseRanges)
    }

  // given an enveloping range and the sub-ranges it overlaps, split out the enveloping range into a
  // set of range segments that are non-overlapping with the children
  let segmentRanges (parentRange: Range) (childRanges: Range[]) : Range[] =
    let firstSegment =
      Range.mkRange parentRange.FileName parentRange.Start childRanges.[0].Start

    let lastSegment =
      Range.mkRange parentRange.FileName (Array.last childRanges).End parentRange.End // from end of last child to end of parent
    // now we can go pairwise, emitting a new range for the area between each end and start
    let innerSegments =
      childRanges
      |> Array.pairwise
      |> Array.map (fun (left, right) -> Range.mkRange parentRange.FileName left.End right.Start)

    [|
       // note that the first and last segments can be zero-length.
       // in that case we should not emit them because it confuses the
       // encoding algorithm
       if Position.posEq firstSegment.Start firstSegment.End then
         ()
       else
         firstSegment
       yield! innerSegments
       if Position.posEq lastSegment.Start lastSegment.End then
         ()
       else
         lastSegment |]

  // TODO: LSP technically does now know how to handle overlapping, nested and multiline ranges, but
  // as of 3 February 2021 there are no good examples of this that I've found, so we still do this
  /// because LSP doesn't know how to handle overlapping/nested ranges, we have to dedupe them here
  let scrubRanges (highlights: SemanticClassificationItem array) : SemanticClassificationItem array =
    let startToken =
      fun (m: SemanticClassificationItem) -> m.Range.StartLine, m.Range.StartColumn

    highlights
    |> Array.sortBy startToken
    |> Array.groupBy (fun m -> m.Range.StartLine)
    |> Array.collect (fun (_, highlights) ->

      // split out any ranges that contain other ranges on this line into the non-overlapping portions of that range
      let expandParents (p: SemanticClassificationItem) =
        let children =
          highlights
          |> Array.except [ p ]
          |> Array.choose (fun child ->
            if Range.rangeContainsRange p.Range child.Range then
              Some child.Range
            else
              None)

        match children with
        | [||] -> [| p |]
        | children ->
          let sortedChildren =
            children |> Array.sortBy (fun r -> r.Start.Line, r.Start.Column)

          segmentRanges p.Range sortedChildren
          |> Array.map (fun subRange -> SemanticClassificationItem((subRange, p.Type)))

      highlights |> Array.collect expandParents)
    |> Array.sortBy startToken

type Commands(checker: FSharpCompilerServiceChecker, state: State, hasAnalyzers: bool, rootPath: string option) =
  let fileParsed = Event<FSharpParseFileResults>()

  let fileChecked = Event<ParseAndCheckResults * string<LocalPath> * int>()

  let scriptFileProjectOptions = Event<FSharpProjectOptions>()

  let disposables = ResizeArray()

  let mutable workspaceRoot: string option = rootPath
  let mutable linterConfigFileRelativePath: string option = None
  // let mutable linterConfiguration: FSharpLint.Application.Lint.ConfigurationParam = FSharpLint.Application.Lint.ConfigurationParam.Default
  let mutable lastCheckResult: ParseAndCheckResults option = None

  let notify = Event<NotificationEvent>()

  let fileStateSet = Event<unit>()

  let mutable isWorkspaceReady = false
  let workspaceReady = Event<unit>()

  let commandsLogger = LogProvider.getLoggerByName "Commands"

  let checkerLogger = LogProvider.getLoggerByName "CheckerEvents"

  let fantomasLogger = LogProvider.getLoggerByName "Fantomas"




  let analyzerHandler (file: string<LocalPath>, content, pt, tast, symbols, getAllEnts) =
    let ctx: SDK.Context =
      { FileName = UMX.untag file
        Content = content
        ParseTree = pt
        TypedTree = tast
        Symbols = symbols
        GetAllEntities = getAllEnts }

    let extractResultsFromAnalyzer (r: SDK.AnalysisResult) =
      match r.Output with
      | Ok results ->
        Loggers.analyzers.info (
          Log.setMessage "Analyzer {analyzer} returned {count} diagnostics for file {file}"
          >> Log.addContextDestructured "analyzer" r.AnalyzerName
          >> Log.addContextDestructured "count" results.Length
          >> Log.addContextDestructured "file" (UMX.untag file)
        )

        results
      | Error e ->
        Loggers.analyzers.error (
          Log.setMessage "Analyzer {analyzer} errored while processing {file}: {message}"
          >> Log.addContextDestructured "analyzer" r.AnalyzerName
          >> Log.addContextDestructured "file" (UMX.untag file)
          >> Log.addContextDestructured "message" e.Message
          >> Log.addExn e
        )

        []

    try
      SDK.Client.runAnalyzersSafely ctx
      |> List.collect extractResultsFromAnalyzer
      |> List.toArray
    with ex ->
      Loggers.analyzers.error (
        Log.setMessage "Error while processing analyzers for {file}: {message}"
        >> Log.addContextDestructured "message" ex.Message
        >> Log.addExn ex
        >> Log.addContextDestructured "file" file
      )

      [||]

  do
    disposables.Add
    <| state.ProjectController.Notifications.Subscribe(NotificationEvent.Workspace >> notify.Trigger)

  //Fill declarations cache so we're able to return workspace symbols correctly
  do
    disposables.Add
    <| fileParsed.Publish.Subscribe(fun parseRes ->
      //TODO: this seems like a large amount of items to keep in-memory like this.
      // Is there a better structure?
      let decls = parseRes.GetNavigationItems().Declarations
      // string<LocalPath> is a compiler-approved path, and since this structure comes from the compiler it's safe
      state.NavigationDeclarations.[UMX.tag parseRes.FileName] <- decls)

  do
    disposables.Add
    <| checker.ScriptTypecheckRequirementsChanged.Subscribe(fun () ->
      checkerLogger.info (Log.setMessage "Script typecheck dependencies changed, purging expired script options")
      let count = state.ScriptProjectOptions.Count
      state.ScriptProjectOptions.Clear()

      checkerLogger.info (
        Log.setMessage "Script typecheck dependencies changed, purged {optionCount} expired script options"
        >> Log.addContextDestructured "optionCount" count
      ))

  //Diagnostics handler - Triggered by `CheckCore`
  do
    disposables.Add
    <| fileChecked.Publish.Subscribe(fun (parseAndCheck, file, _) ->
      async {
        try
          NotificationEvent.FileParsed file |> notify.Trigger

          let checkErrors = parseAndCheck.GetParseResults.Diagnostics

          let parseErrors = parseAndCheck.GetCheckResults.Diagnostics

          let errors =
            Array.append checkErrors parseErrors
            |> Array.distinctBy (fun e ->
              e.Severity, e.ErrorNumber, e.StartLine, e.StartColumn, e.EndLine, e.EndColumn, e.Message)

          (errors, file) |> NotificationEvent.ParseError |> notify.Trigger
        with _ ->
          ()
      }
      |> Async.Start)

  //Analyzers handler - Triggered by `CheckCore`
  do
    disposables.Add
    <| fileChecked.Publish.Subscribe(fun (parseAndCheck, file, _) ->
      async {
        if hasAnalyzers then
          try
            Loggers.analyzers.info (
              Log.setMessage "begin analysis of {file}"
              >> Log.addContextDestructured "file" file
            )

            match parseAndCheck.GetCheckResults.ImplementationFile with
            | Some tast ->
              match state.Files.TryGetValue file with
              | true, fileData ->

                let res =
                  analyzerHandler (
                    file,
                    fileData.Lines.ToString().Split("\n"),
                    parseAndCheck.GetParseResults.ParseTree,
                    tast,
                    parseAndCheck.GetCheckResults.PartialAssemblySignature.Entities |> Seq.toList,
                    parseAndCheck.GetAllEntities
                  )

                (res, file) |> NotificationEvent.AnalyzerMessage |> notify.Trigger

                Loggers.analyzers.info (
                  Log.setMessage "end analysis of {file}"
                  >> Log.addContextDestructured "file" file
                )
              | false, _ ->
                let otherKeys = state.Files.Keys |> Array.ofSeq

                Loggers.analyzers.info (
                  Log.setMessage "No file contents found for {file}. Current files are {files}"
                  >> Log.addContextDestructured "file" file
                  >> Log.addContextDestructured "files" otherKeys
                )
            | _ ->
              Loggers.analyzers.info (
                Log.setMessage "missing components of {file} to run analyzers, skipped them"
                >> Log.addContextDestructured "file" file
              )

              ()
          with ex ->
            Loggers.analyzers.error (
              Log.setMessage "Run failed for {file}"
              >> Log.addContextDestructured "file" file
              >> Log.addExn ex
            )
      }
      |> Async.Start)

    //Test detection handler
    do
      disposables.Add
      <| fileParsed.Publish.Subscribe(fun parseResults ->
        commandsLogger.info (
          Log.setMessage "Test Detection of {file} started"
          >> Log.addContextDestructured "file" parseResults.FileName
        )

        let fn = UMX.tag parseResults.FileName

        match state.GetProjectOptions fn with
        | None ->
          commandsLogger.info (
            Log.setMessage "Test Detection of {file} - no project file"
            >> Log.addContextDestructured "file" parseResults.FileName
          )
        | Some proj ->
          let res =
            if proj.OtherOptions |> Seq.exists (fun o -> o.Contains "Expecto.dll") then
              TestAdapter.getExpectoTests parseResults.ParseTree
            elif proj.OtherOptions |> Seq.exists (fun o -> o.Contains "nunit.framework.dll") then
              TestAdapter.getNUnitTest parseResults.ParseTree
            elif proj.OtherOptions |> Seq.exists (fun o -> o.Contains "xunit.assert.dll") then
              TestAdapter.getXUnitTest parseResults.ParseTree
            else
              []

          commandsLogger.info (
            Log.setMessage "Test Detection of {file} - {res}"
            >> Log.addContextDestructured "file" parseResults.FileName
            >> Log.addContextDestructured "res" res
          )

          NotificationEvent.TestDetected(fn, res |> List.toArray) |> notify.Trigger)

  let parseFilesInTheBackground files =
    async {
      let rec loopForProjOpts file =
        async {
          match state.GetProjectOptions file with
          | None ->
            do! Async.Sleep(TimeSpan.FromSeconds 1.)
            return! loopForProjOpts file
          | Some opts -> return Utils.projectOptionsToParseOptions opts
        }

      for file in files do
        try
          let sourceOpt =
            match state.Files.TryFind file with
            | Some f -> Some(f.Lines)
            | None when File.Exists(UMX.untag file) ->
              let ctn = File.ReadAllText(UMX.untag file)
              let text = NamedText(file, ctn)

              state.Files.[file] <-
                { Touched = DateTime.Now
                  Lines = text
                  Version = None }

              Some text
            | None -> None

          match sourceOpt with
          | None -> ()
          | Some source ->
            async {
              let! opts = loopForProjOpts file
              let! parseRes = checker.ParseFile(file, source, opts)
              fileParsed.Trigger parseRes
            }
            |> Async.Start
        with ex ->
          commandsLogger.error (
            Log.setMessage "Failed to parse file '{file}'"
            >> Log.addContextDestructured "file" file
            >> Log.addExn ex
          )
    }

  let codeGenServer = CodeGenerationService(checker, state)


  let fillHelpTextInTheBackground decls (pos: Position) fn getLine =
    let declName (d: DeclarationListItem) = d.Name

    //Fill list of declarations synchronously to know which declarations should be in cache.
    for d in decls do
      state.Declarations.[declName d] <- (d, pos, fn)

    //Fill namespace insertion cache asynchronously.
    async {
      for decl in decls do
        let n = declName decl

        match Commands.calculateNamespaceInsert (fun () -> state.CurrentAST) decl pos getLine with
        | Some insert -> state.CompletionNamespaceInsert.[n] <- insert
        | None -> ()
    }
    |> Async.Start

  let fantomasService: FantomasService = new LSPFantomasService() :> FantomasService

  do disposables.Add fantomasService

  do
    disposables.Add
    <| state.ProjectController.Notifications.Subscribe(fun ev ->
      match ev with
      | ProjectResponse.Project (p, isFromCache) ->
        if not isFromCache then
          p.ProjectItems
          |> List.choose (function
            | ProjectViewerItem.Compile (p, _) -> Some(Utils.normalizePath p))
          |> parseFilesInTheBackground
          |> Async.Start
        else
          commandsLogger.info (
            Log.setMessage "Project from cache '{file}'"
            >> Log.addContextDestructured "file" p.ProjectFileName
          )
      | _ -> ())

  member __.Notify = notify.Publish

  member __.FileChecked = fileChecked.Publish

  member __.ScriptFileProjectOptions = scriptFileProjectOptions.Publish

  member __.LastCheckResult = lastCheckResult

  member __.SetFileContent(file: string<LocalPath>, lines: NamedText, version) = state.AddFileText(file, lines, version)

  member private x.MapResultAsync
    (
      successToString: 'a -> Async<CoreResponse<'b>>,
      ?failureToString: string -> CoreResponse<'b>
    ) =
    Async.bind
    <| function
      // A failure is only info here, as this command is expected to be
      // used 'on idle', and frequent errors are expected.
      | ResultOrString.Error e -> async.Return((defaultArg failureToString CoreResponse.InfoRes) e)
      | ResultOrString.Ok r -> successToString r

  member private x.MapResult(successToString: 'a -> CoreResponse<'b>, ?failureToString: string -> CoreResponse<'b>) =
    x.MapResultAsync((fun x -> successToString x |> async.Return), ?failureToString = failureToString)

  member x.Fsdn(querystr) =
    async {
      let! results = Fsdn.query querystr
      return CoreResponse.Res results
    }

  static member DotnetNewList() =
    async {
      let results = DotnetNewTemplate.installedTemplates ()
      return CoreResponse.Res results
    }


  static member DotnetNewRun
    (templateShortName: string)
    (name: string option)
    (output: string option)
    (parameterStr: (string * obj) list)
    =
    async {
      let! results = DotnetCli.dotnetNew templateShortName name output parameterStr
      return CoreResponse.Res results
    }

  static member DotnetAddProject (toProject: string) (reference: string) =
    async {
      let! result = DotnetCli.dotnetAddProject toProject reference
      return CoreResponse.Res result
    }

  static member DotnetRemoveProject (fromProject: string) (reference: string) =
    async {
      let! result = DotnetCli.dotnetRemoveProject fromProject reference
      return CoreResponse.Res result
    }

  static member DotnetSlnAdd (sln: string) (project: string) =
    async {
      let! result = DotnetCli.dotnetSlnAdd sln project
      return CoreResponse.Res result
    }

  static member FsProjMoveFileUp (fsprojPath: string) (fileVirtPath: string) =
    async {
      FsProjEditor.moveFileUp fsprojPath fileVirtPath
      return CoreResponse.Res()
    }

  static member FsProjMoveFileDown (fsprojPath: string) (fileVirtPath: string) =
    async {
      FsProjEditor.moveFileDown fsprojPath fileVirtPath
      return CoreResponse.Res()
    }

  member _.FsProjAddFileAbove (fsprojPath: string) (fileVirtPath: string) (newFileName: string) =
    async {
      try
        let dir = Path.GetDirectoryName fsprojPath
        let virtPathDir = Path.GetDirectoryName fileVirtPath

        let newFilePath = Path.Combine(dir, virtPathDir, newFileName)
        let fileInfo = FileInfo(newFilePath)

        // Ensure the destination directory exist
        if not fileInfo.Directory.Exists then
          fileInfo.Directory.Create()

        (File.Open(newFilePath, FileMode.OpenOrCreate)).Close()

        let newVirtPath = Path.Combine(virtPathDir, newFileName)
        FsProjEditor.addFileAbove fsprojPath fileVirtPath newVirtPath
        return CoreResponse.Res()
      with ex ->
        return CoreResponse.ErrorRes ex.Message
    }

  member _.FsProjAddFileBelow (fsprojPath: string) (fileVirtPath: string) (newFileName: string) =
    async {
      try
        let dir = Path.GetDirectoryName fsprojPath
        let virtPathDir = Path.GetDirectoryName fileVirtPath

        let newFilePath = Path.Combine(dir, virtPathDir, newFileName)
        let fileInfo = FileInfo(newFilePath)

        // Ensure the destination directory exist
        if not fileInfo.Directory.Exists then
          fileInfo.Directory.Create()

        (File.Open(newFilePath, FileMode.OpenOrCreate)).Close()

        let newVirtPath = Path.Combine(virtPathDir, newFileName)
        FsProjEditor.addFileBelow fsprojPath fileVirtPath newVirtPath
        return CoreResponse.Res()
      with ex ->
        return CoreResponse.ErrorRes ex.Message
    }

  member _.FsProjAddFile (fsprojPath: string) (fileVirtPath: string) =
    async {
      try
        let dir = Path.GetDirectoryName fsprojPath
        let newFilePath = Path.Combine(dir, fileVirtPath)
        let fileInfo = FileInfo(newFilePath)

        // Ensure the destination directory exist
        if not fileInfo.Directory.Exists then
          fileInfo.Directory.Create()

        (File.Open(newFilePath, FileMode.OpenOrCreate)).Close()

        FsProjEditor.addFile fsprojPath fileVirtPath
        return CoreResponse.Res()
      with ex ->
        return CoreResponse.ErrorRes ex.Message
    }

  member _.FsProjAddExistingFile (fsprojPath: string) (fileVirtPath: string) =
    async {
      FsProjEditor.addExistingFile fsprojPath fileVirtPath
      return CoreResponse.Res()
    }

  member _.FsProjRemoveFile (fsprojPath: string) (fileVirtPath: string) (fullPath: string) =
    async {
      FsProjEditor.removeFile fsprojPath fileVirtPath
      state.RemoveProjectOptions(normalizePath fullPath)
      return CoreResponse.Res()
    }

  member inline private x.AsCancellable (filename: string<LocalPath>) (action: Async<'t>) =
    let cts = new CancellationTokenSource()
    state.AddCancellationToken(filename, cts)

    Async.StartCatchCancellation(action, cts.Token)
    |> Async.Catch
    |> Async.map (function
      | Choice1Of2 res -> Ok res
      | Choice2Of2 err ->
        notify.Trigger(NotificationEvent.Canceled(sprintf "Request cancelled (exn was %A)" err))
        Error err)


  member private x.CancelQueue(filename: string<LocalPath>) =
    state.GetCancellationTokens filename |> List.iter (fun cts -> cts.Cancel())

  member x.TryGetRecentTypeCheckResultsForFile(file: string<LocalPath>) =
    match state.TryGetFileCheckerOptionsWithLines file with
    | Ok (opts, text) -> x.TryGetRecentTypeCheckResultsForFile(file, opts, text)
    | _ -> async.Return None

  member x.TryGetRecentTypeCheckResultsForFile(file, opts, text) =
    async {
      match checker.TryGetRecentCheckResultsForFile(file, opts, text) with
      | None ->
        commandsLogger.debug (
          Log.setMessage "GetRecent/3 none '{file}'"
          >> Log.addContextDestructured "file" file
        )
        let version = state.TryGetFileVersion file |> Option.defaultValue 0

        match! checker.ParseAndCheckFileInProject(file, version, text, opts) with
        | Ok r -> return Some r
        | Error _ -> return None
      | Some r ->
        commandsLogger.debug (
          Log.setMessage "GetRecent/3 some '{file}'"
          >> Log.addContextDestructured "file" file
        )
        return Some r
    }

  member x.TryGetFileCheckerOptionsWithLinesAndLineStr(file: string<LocalPath>, pos) =
    state.TryGetFileCheckerOptionsWithLinesAndLineStr(file, pos)

  member x.TryGetFileCheckerOptionsWithLines(file: string<LocalPath>) =
    state.TryGetFileCheckerOptionsWithLines file

  member x.TryGetFileVersion = state.TryGetFileVersion

  /// Gets the current project options for the given file.
  /// If the file is a script, determines if the file content is changed enough to warrant new project options,
  /// and if so registers them.
  member x.EnsureProjectOptionsForFile(file: string<LocalPath>, text: NamedText, version, fsiRefs) =
    async {
      do! state.WaitForWorkspaceReady()

      match state.GetProjectOptions(file) with
      | Some opts ->
        match state.RefreshCheckerOptions(file, text) with
        | Some c ->
          state.SetFileVersion file version
          fileStateSet.Trigger()
          return Some opts
        | None -> return None
      | None when Utils.isAScript (UMX.untag file) -> // scripts won't have project options in the 'core' project options collection
        let hash =
          text.Lines
          |> Array.filter (fun n -> n.StartsWith "#r" || n.StartsWith "#load" || n.StartsWith "#I")
          |> Array.toList
          |> fun n -> n.GetHashCode()

        let! projectOptions =
          match state.ScriptProjectOptions.TryFind file with
          | Some (h, opts) when h = hash -> async.Return opts
          | _ ->
            async {
              let! projectOptions = checker.GetProjectOptionsFromScript(file, text, fsiRefs)

              let projectOptions =
                { projectOptions with
                    SourceFiles =
                      projectOptions.SourceFiles
                      |> Array.filter FscArguments.isCompileFile
                      |> Array.map (Path.GetFullPath)
                    OtherOptions =
                      projectOptions.OtherOptions
                      |> Array.map (fun n ->
                        if FscArguments.isCompileFile (n) then
                          Path.GetFullPath n
                        else
                          n) }

              state.ScriptProjectOptions.AddOrUpdate(file, (hash, projectOptions), (fun _ _ -> (hash, projectOptions)))
              |> ignore

              return projectOptions
            }

        scriptFileProjectOptions.Trigger projectOptions
        state.AddFileTextAndCheckerOptions(file, text, projectOptions, Some version)
        fileStateSet.Trigger()
        return Some projectOptions
      | None ->
        // this is a loose .fs file without a project?
        return None
    }

  /// Does everything required to check a file:
  /// * ensure we have project options for the file available
  /// * check any unchecked files in the project that this file depends on
  /// * check the file
  /// * check the other files in the project that depend on this file
  /// * check projects that are downstream of the project containing this file
  member x.CheckFile
    (
      file: string<LocalPath>,
      version,
      content,
      tfmConfig,
      checkFilesThatThisFileDependsOn,
      checkFilesThatDependsOnFile,
      checkDependentProjects
    ) : Async<unit> =
    async {
      match! x.EnsureProjectOptionsForFile(file, content, version, tfmConfig) with
      | None -> ()
      | Some opts ->
        // parse dependent  files, if necessary
        if checkFilesThatThisFileDependsOn then
          do!
            opts.SourceFilesThatThisFileDependsOn(file)
            |> Array.map (UMX.tag >> (fun f -> x.SimpleCheckFile(f, tfmConfig)))
            |> Async.Sequential
            |> Async.map ignore<unit[]>

        // parse this file
        do! x.CheckFile(file, content, version, opts)

        // then parse all files that depend on it in this project,
        if checkFilesThatDependsOnFile then
          do!
            opts.SourceFilesThatDependOnFile(file)
            |> Array.map (UMX.tag >> (fun f -> x.SimpleCheckFile(f, tfmConfig)))
            |> Async.Sequential
            |> Async.map ignore<unit[]>

        // then parse all files in dependent projects
        if checkDependentProjects then
          do!
            state.ProjectController.GetDependentProjectsOfProjects([ opts ])
            |> List.map x.CheckProject
            |> Async.Sequential
            |> Async.map ignore<unit[]>
    }

  /// Does everything required to check a file:
  /// * ensure we have project options for the file available
  /// * check any unchecked files in the project that this file depends on
  /// * check the file
  /// * check the other files in the project that depend on this file
  /// * check projects that are downstream of the project containing this file
  member x.CheckFileAndAllDependentFilesInAllProjects
    (
      file: string<LocalPath>,
      version: int,
      content: NamedText,
      tfmConfig: FSIRefs.TFM,
      isFirstOpen: bool
    ) : Async<unit> =
    x.CheckFile(
      file,
      version,
      content,
      tfmConfig,
      checkFilesThatThisFileDependsOn = isFirstOpen,
      checkFilesThatDependsOnFile = true,
      // TODO: Disabled due to performance issues - investigate.
      checkDependentProjects = false
    )

  /// easy helper that looks up a file and all required checking information then checks it.
  /// intended use is from the other, more complex Parse members, because the filePath is untagged
  member private x.SimpleCheckFile(filePath: string<LocalPath>, tfmIfScript) : Async<unit> =
    async {
      match state.TryGetFileSource filePath with
      | Ok text ->

        let version = state.TryGetFileVersion filePath |> Option.defaultValue 0

        let! options = x.EnsureProjectOptionsForFile(filePath, text, version, tfmIfScript)

        match options with
        | Some options -> do! x.CheckFile(filePath, text, version, options)
        | None -> ()
      | Error err -> ()
    }

  member private _.CheckCore(fileName: string<LocalPath>, version, text, options) : Async<unit> =
    async {
      match! checker.ParseAndCheckFileInProject(fileName, version, text, options) with
      | Ok parseAndCheck ->
        do lastCheckResult <- Some parseAndCheck
        do state.SetLastCheckedVersion fileName version
        do fileParsed.Trigger parseAndCheck.GetParseResults
        do fileChecked.Trigger(parseAndCheck, fileName, version)
      | Error e -> ()
    }

  member x.CheckProject(p: FSharpProjectOptions) : Async<unit> =
    p.SourceFiles
    |> Array.map (UMX.tag >> (fun f -> x.SimpleCheckFile(f, FSIRefs.TFM.NetCore)))
    |> Async.Sequential
    |> Async.map ignore<unit[]>

  member private x.CheckFile(file, text: NamedText, version: int, projectOptions: FSharpProjectOptions) : Async<unit> =
    async {
      do x.CancelQueue file
      return! x.CheckCore(file, version, text, projectOptions)
    }

  member x.Declarations (file: string<LocalPath>) lines version =
    async {
      match state.TryGetFileCheckerOptionsWithSource file, lines with
      | ResultOrString.Error s, None ->
        match state.TryGetFileSource file with
        | ResultOrString.Error s -> return CoreResponse.ErrorRes s
        | ResultOrString.Ok text ->
          let files = Array.singleton (UMX.untag file)

          let parseOptions = { FSharpParsingOptions.Default with SourceFiles = files }

          let! decls = checker.GetDeclarations(file, text, parseOptions, version)
          let decls = decls |> Array.map (fun a -> a, file)
          return CoreResponse.Res decls
      | ResultOrString.Error _, Some text ->
        let files = Array.singleton (UMX.untag file)

        let parseOptions = { FSharpParsingOptions.Default with SourceFiles = files }

        let! decls = checker.GetDeclarations(file, text, parseOptions, version)
        let decls = decls |> Array.map (fun a -> a, file)
        return CoreResponse.Res decls
      | ResultOrString.Ok (checkOptions, text), _ ->
        let parseOptions = Utils.projectOptionsToParseOptions checkOptions

        let! decls = checker.GetDeclarations(file, text, parseOptions, version)

        state.NavigationDeclarations.[file] <- decls

        let decls = decls |> Array.map (fun a -> a, file)
        return CoreResponse.Res decls
    }

  member x.DeclarationsInProjects() =
    async {
      let decls =
        state.NavigationDeclarations.ToArray()
        |> Array.collect (fun (KeyValue (p, decls)) -> decls |> Array.map (fun d -> d, p))

      return CoreResponse.Res decls
    }

  member _.Helptext sym =
    async {
      match KeywordList.keywordDescriptions.TryGetValue sym with
      | true, s -> return CoreResponse.Res(HelpText.Simple(sym, s))
      | _ ->
        match KeywordList.hashDirectives.TryGetValue sym with
        | true, s -> return CoreResponse.Res(HelpText.Simple(sym, s))
        | _ ->
          let sym =
            if sym.StartsWith "``" && sym.EndsWith "``" then
              sym.TrimStart([| '`' |]).TrimEnd([| '`' |])
            else
              sym

          match state.Declarations.TryFind sym with
          | None -> //Isn't in sync filled cache, we don't have result
            return CoreResponse.ErrorRes(sprintf "No help text available for symbol '%s'" sym)
          | Some (decl, pos, fn) -> //Is in sync filled cache, try to get results from async filled caches or calculate if it's not there
            let source = state.Files.TryFind fn |> Option.map (fun n -> n.Lines)

            match source with
            | None -> return CoreResponse.ErrorRes(sprintf "No help text available for symbol '%s'" sym)
            | Some source ->
              let tip =
                match state.HelpText.TryFind sym with
                | Some tip -> tip
                | None ->
                  let tip = decl.Description
                  state.HelpText.[sym] <- tip
                  tip

              let n =
                match state.CompletionNamespaceInsert.TryFind sym with
                | None -> Commands.calculateNamespaceInsert (fun () -> state.CurrentAST) decl pos source.GetLine
                | Some s -> Some s

              return CoreResponse.Res(HelpText.Full(sym, tip, n))
    }

  static member CompilerLocation(checker: FSharpCompilerServiceChecker) =
    CoreResponse.Res(Environment.fsc, Environment.fsi, Some "", checker.GetDotnetRoot())

  member x.CompilerLocation() = Commands.CompilerLocation(checker)

  member x.Colorization enabled = state.ColorizationOutput <- enabled
  member x.Error msg = [ CoreResponse.ErrorRes msg ]

  member _.Completion
    (tyRes: ParseAndCheckResults)
    (pos: Position)
    lineStr
    (lines: NamedText)
    (fileName: string<LocalPath>)
    filter
    includeKeywords
    includeExternal
    =
    async {
      let getAllSymbols () =
        if includeExternal then tyRes.GetAllEntities true else []

      let! res = tyRes.TryGetCompletions pos lineStr filter getAllSymbols

      match res with
      | Some (decls, residue, shouldKeywords) ->
        let declName (d: DeclarationListItem) = d.Name

        //Init cache for current list
        state.Declarations.Clear()
        state.HelpText.Clear()
        state.CompletionNamespaceInsert.Clear()
        state.CurrentAST <- Some tyRes.GetAST

        //Fill cache for current list
        do fillHelpTextInTheBackground decls pos fileName lines.GetLine

        // Send the first help text without being requested.
        // This allows it to be displayed immediately in the editor.
        // let firstMatchOpt =
        //   decls
        //   |> Array.sortBy declName
        //   |> Array.tryFind (fun d -> (declName d).StartsWith(residue, StringComparison.InvariantCultureIgnoreCase))

        let includeKeywords = includeKeywords && shouldKeywords

        return CoreResponse.Res(decls, includeKeywords)

      | None -> return CoreResponse.ErrorRes "Timed out while fetching completions"
    }

  member x.ToolTip (tyRes: ParseAndCheckResults) (pos: Position) lineStr =
    tyRes.TryGetToolTipEnhanced pos lineStr
    |> Result.bimap CoreResponse.Res CoreResponse.ErrorRes

  static member FormattedDocumentation (tyRes: ParseAndCheckResults) (pos: Position) lineStr =
    tyRes.TryGetFormattedDocumentation pos lineStr
    |> Result.bimap CoreResponse.Res CoreResponse.ErrorRes

  static member FormattedDocumentationForSymbol (tyRes: ParseAndCheckResults) (xmlSig: string) (assembly: string) =
    tyRes.TryGetFormattedDocumentationForSymbol xmlSig assembly
    |> Result.bimap CoreResponse.Res CoreResponse.ErrorRes

  member x.Typesig (tyRes: ParseAndCheckResults) (pos: Position) lineStr = Commands.typesig tyRes pos lineStr

  member x.SymbolUse (tyRes: ParseAndCheckResults) (pos: Position) lineStr =
    tyRes.TryGetSymbolUseAndUsages pos lineStr
    |> Result.bimap CoreResponse.Res CoreResponse.ErrorRes

  static member SignatureData (tyRes: ParseAndCheckResults) (pos: Position) lineStr =
    tyRes.TryGetSignatureData pos lineStr
    |> Result.bimap CoreResponse.Res CoreResponse.ErrorRes

  static member Help (tyRes: ParseAndCheckResults) (pos: Position) lineStr =
    tyRes.TryGetF1Help pos lineStr
    |> Result.bimap CoreResponse.Res CoreResponse.ErrorRes

  /// for a given member, use its signature information to generate placeholder XML documentation strings.
  /// calculates the required indent and gives the position to insert the text.
  static member GenerateXmlDocumentation(tyRes: ParseAndCheckResults, triggerPosition: Position, lineStr: LineStr) =
    asyncResult {
      let trimmed = lineStr.TrimStart(' ')
      let indentLength = lineStr.Length - trimmed.Length
      let indentString = String.replicate indentLength " "

      let! (_, memberParameters, genericParameters) =
        Commands.SignatureData tyRes triggerPosition lineStr |> Result.ofCoreResponse

      let summarySection = "/// <summary></summary>"

      let parameterSection (name, _type) =
        $"/// <param name=\"%s{name}\"></param>"

      let genericArg name =
        $"/// <typeparam name=\"'%s{name}\"></typeparam>"

      let returnsSection = "/// <returns></returns>"

      let formattedXmlDoc =
        seq {
          yield summarySection

          match memberParameters with
          | [] -> ()
          | parameters ->
            yield!
              parameters
              |> List.concat
              |> List.mapi (fun _index parameter -> parameterSection parameter)

          match genericParameters with
          | [] -> ()
          | generics -> yield! generics |> List.mapi (fun _index generic -> genericArg generic)

          yield returnsSection
        }
        |> Seq.map (fun s -> indentString + s)
        |> String.concat Environment.NewLine
        |> fun s -> s + Environment.NewLine // need a newline at the very end

      // always insert at the start of the line, because we've prepended the indent to the start of the summary section
      let insertPosition = Position.mkPos triggerPosition.Line 0

      return
        { InsertPosition = insertPosition
          InsertText = formattedXmlDoc }
    }

  member x.SymbolUseWorkspace(pos, lineStr, text: NamedText, tyRes: ParseAndCheckResults) =
    asyncResult {

      let getDeclarationLocation (symUse, text) =
        SymbolLocation.getDeclarationLocation (
          symUse,
          text,
          state.GetProjectOptions,
          state.ProjectController.ProjectsThatContainFile,
          state.ProjectController.GetDependentProjectsOfProjects
        )

      let findReferencesForSymbolInFile (file, project, symbol) =
        checker.FindReferencesForSymbolInFile(file, project, symbol)

      let tryGetFileSource symbolFile = state.TryGetFileSource(symbolFile)

      let getProjectOptionsForFsproj fsprojPath =
        state.ProjectController.GetProjectOptionsForFsproj fsprojPath

      return!
        Commands.symbolUseWorkspace
          getDeclarationLocation
          findReferencesForSymbolInFile
          tryGetFileSource
          getProjectOptionsForFsproj
          pos
          lineStr
          text
          tyRes
    }

  member x.RenameSymbol(pos: Position, tyRes: ParseAndCheckResults, lineStr: LineStr, text: NamedText) =
    asyncResult {
      let symbolUseWorkspace pos lineStr text tyRes =
        x.SymbolUseWorkspace(pos, lineStr, text, tyRes)

      let tryGetFileSource filePath = state.TryGetFileSource filePath
      return! Commands.renameSymbol symbolUseWorkspace tryGetFileSource pos tyRes lineStr text
    }

  member x.SymbolImplementationProject (tyRes: ParseAndCheckResults) (pos: Position) lineStr =
    let getProjectOptions filePath = state.GetProjectOptions' filePath

    let getUsesOfSymbol (filePath, opts, sym: FSharpSymbol) =
      checker.GetUsesOfSymbol(filePath, opts, sym)

    let getAllProjects () =
      state.FSharpProjectOptions |> Seq.toList

    Commands.symbolImplementationProject getProjectOptions getUsesOfSymbol getAllProjects tyRes pos lineStr
    |> x.AsCancellable tyRes.FileName
    |> AsyncResult.recoverCancellation

  member x.FindDeclaration (tyRes: ParseAndCheckResults) (pos: Position) lineStr =
    tyRes.TryFindDeclaration pos lineStr
    |> x.MapResult(CoreResponse.Res, CoreResponse.ErrorRes)
    |> x.AsCancellable tyRes.FileName
    |> AsyncResult.recoverCancellation

  member x.FindTypeDeclaration (tyRes: ParseAndCheckResults) (pos: Position) lineStr =
    tyRes.TryFindTypeDeclaration pos lineStr
    |> x.MapResult(CoreResponse.Res, CoreResponse.ErrorRes)
    |> x.AsCancellable tyRes.FileName
    |> AsyncResult.recoverCancellation

  /// Attempts to identify member overloads and infer current parameter positions for signature help at a given location
  member x.MethodsForSignatureHelp
    (
      tyRes: ParseAndCheckResults,
      pos: Position,
      lines: NamedText,
      triggerChar,
      possibleSessionKind
    ) =
    SignatureHelp.getSignatureHelpFor (tyRes, pos, lines, triggerChar, possibleSessionKind)

  // member x.Lint (file: string<LocalPath>): Async<unit> =
  //     asyncResult {
  //       let! (options, source) = state.TryGetFileCheckerOptionsWithSource file
  //       match checker.TryGetRecentCheckResultsForFile(file, options) with
  //       | None -> return ()
  //       | Some tyRes ->
  //         match tyRes.GetAST with
  //         | None -> return ()
  //         | Some tree ->
  //           try
  //             let! ctok = Async.CancellationToken
  //             let! enrichedWarnings = Lint.lintWithConfiguration linterConfiguration ctok tree source tyRes.GetCheckResults
  //             let res = CoreResponse.Res (file, enrichedWarnings)
  //             notify.Trigger (NotificationEvent.Lint (file, enrichedWarnings))
  //             return ()
  //           with ex ->
  //             commandsLogger.error (Log.setMessage "error while linting {file}: {message}"
  //                                   >> Log.addContextDestructured "file" file
  //                                   >> Log.addContextDestructured "message" ex.Message
  //                                   >> Log.addExn ex)
  //             return ()
  //     }
  //     |> Async.Ignore
  //     |> x.AsCancellable file

  //     |> AsyncResult.recoverCancellationIgnore

  member x.GetNamespaceSuggestions (tyRes: ParseAndCheckResults) (pos: Position) (line: LineStr) =
    Commands.getNamespaceSuggestions tyRes pos line
    |> x.AsCancellable tyRes.FileName
    |> AsyncResult.recoverCancellation

  member x.GetUnionPatternMatchCases
    (tyRes: ParseAndCheckResults)
    (pos: Position)
    (lines: ISourceText)
    (line: LineStr)
    =
    async {
      let tryFindUnionDefinitionFromPos = tryFindUnionDefinitionFromPos codeGenServer
      return! Commands.getUnionPatternMatchCases tryFindUnionDefinitionFromPos tyRes pos lines line
    }
    |> x.AsCancellable tyRes.FileName
    |> AsyncResult.recoverCancellation

  member x.GetRecordStub (tyRes: ParseAndCheckResults) (pos: Position) (lines: NamedText) (line: LineStr) =

    Commands.getRecordStub (tryFindRecordDefinitionFromPos codeGenServer) tyRes pos lines line
    |> x.AsCancellable tyRes.FileName
    |> AsyncResult.recoverCancellation

  member x.GetAbstractClassStub
    (tyRes: ParseAndCheckResults)
    (objExprRange: Range)
    (lines: NamedText)
    (lineStr: LineStr)
    =
    let tryFindAbstractClassExprInBufferAtPos =
      AbstractClassStubGenerator.tryFindAbstractClassExprInBufferAtPos codeGenServer

    let writeAbstractClassStub =
      AbstractClassStubGenerator.writeAbstractClassStub codeGenServer

    Commands.getAbstractClassStub
      tryFindAbstractClassExprInBufferAtPos
      writeAbstractClassStub
      tyRes
      objExprRange
      lines
      lineStr
    |> AsyncResult.foldResult id id
    |> x.AsCancellable tyRes.FileName
    |> AsyncResult.recoverCancellation

  member x.WorkspacePeek (dir: string) (deep: int) (excludedDirs: string list) =
    async {
      let d = state.ProjectController.PeekWorkspace(dir, deep, excludedDirs)

      return CoreResponse.Res d
    }

  member x.WorkspaceLoad (files: string list) (disableInMemoryProjectReferences: bool) binaryLogs =
    async {
      commandsLogger.info (
        Log.setMessage "Workspace loading started '{files}'"
        >> Log.addContextDestructured "files" files
      )

      checker.DisableInMemoryProjectReferences <- disableInMemoryProjectReferences
      let! response = state.ProjectController.LoadWorkspace(files, binaryLogs)
      commandsLogger.info (Log.setMessage "Workspace loading finished")

      isWorkspaceReady <- true
      workspaceReady.Trigger()

      return CoreResponse.Res response
    }

  member x.Project projectFileName binaryLogs =
    async {
      commandsLogger.info (
        Log.setMessage "Project loading '{file}'"
        >> Log.addContextDestructured "file" projectFileName
      )

      let! response = state.ProjectController.LoadProject(projectFileName, binaryLogs)
      return CoreResponse.Res response
    }

  member x.CheckUnusedDeclarations(file: string<LocalPath>) : Async<unit> =
    asyncResult {
      let isScript = Utils.isAScript (UMX.untag file)

      let! (opts, source) = state.TryGetFileCheckerOptionsWithSource file

      let tyResOpt = checker.TryGetRecentCheckResultsForFile(file, opts, source)

      match tyResOpt with
      | None -> ()
      | Some tyRes ->
        let! unused = UnusedDeclarations.getUnusedDeclarations (tyRes.GetCheckResults, isScript)
        let unused = unused |> Seq.toArray

        notify.Trigger(NotificationEvent.UnusedDeclarations(file, unused))
    }
    |> Async.Ignore<Result<unit, _>>

  member x.CheckSimplifiedNames file : Async<unit> =
    asyncResult {
      let! (opts, source) = state.TryGetFileCheckerOptionsWithLines file

      let tyResOpt = checker.TryGetRecentCheckResultsForFile(file, opts, source)

      match tyResOpt with
      | None -> ()
      | Some tyRes ->
        let getSourceLine lineNo =
          (source :> ISourceText).GetLineString(lineNo - 1)

        let! simplified = SimplifyNames.getSimplifiableNames (tyRes.GetCheckResults, getSourceLine)
        let simplified = Array.ofSeq simplified
        notify.Trigger(NotificationEvent.SimplifyNames(file, simplified))
    }
    |> Async.Ignore<Result<unit, _>>
    |> x.AsCancellable file
    |> AsyncResult.recoverCancellationIgnore

  member x.CheckUnusedOpens file : Async<unit> =
    asyncResult {
      let! (opts, source) = state.TryGetFileCheckerOptionsWithLines file

      match checker.TryGetRecentCheckResultsForFile(file, opts, source) with
      | None -> return ()
      | Some tyRes ->
        let! unused =
          UnusedOpens.getUnusedOpens (tyRes.GetCheckResults, (fun i -> (source: ISourceText).GetLineString(i - 1)))

        notify.Trigger(NotificationEvent.UnusedOpens(file, (unused |> List.toArray)))

    }
    |> Async.Ignore<Result<unit, _>>
    |> x.AsCancellable file
    |> AsyncResult.recoverCancellationIgnore


  member x.GetRangesAtPosition file positions =
    let getParseResultsForFile file =
      asyncResult {
        let! (opts, text) = state.TryGetFileCheckerOptionsWithLines file
        let parseOpts = Utils.projectOptionsToParseOptions opts
        let! ast = checker.ParseFile(file, text, parseOpts)
        return ast
      }

    Commands.getRangesAtPosition getParseResultsForFile file positions

  member x.GetGitHash() =
    let version = Version.info ()
    version.GitSha

  member __.Quit() =
    async { return [ CoreResponse.InfoRes "quitting..." ] }

  member x.ScopesForFile(file: string<LocalPath>) =
    let getParseResultsForFile file =
      asyncResult {
        let! (opts, text) = state.TryGetFileCheckerOptionsWithLines file
        let parseOpts = Utils.projectOptionsToParseOptions opts
        let! ast = checker.ParseFile(file, text, parseOpts)
        return text, ast
      }

    Commands.scopesForFile getParseResultsForFile file


  member __.SetDotnetSDKRoot(dotnetBinary: System.IO.FileInfo) =
    checker.SetDotnetRoot(dotnetBinary, defaultArg rootPath System.Environment.CurrentDirectory |> DirectoryInfo)

  member __.SetFSIAdditionalArguments args = checker.SetFSIAdditionalArguments args

  member x.FormatDocument(file: string<LocalPath>) : Async<Result<FormatDocumentResponse, string>> =
    let tryGetFileCheckerOptionsWithLines file =
      x.TryGetFileCheckerOptionsWithLines file |> Result.map snd

    let formatDocumentAsync x = fantomasService.FormatDocumentAsync x
    Commands.formatDocument tryGetFileCheckerOptionsWithLines formatDocumentAsync file

  member x.FormatSelection
    (
      file: string<LocalPath>,
      rangeToFormat: FormatSelectionRange
    ) : Async<Result<FormatDocumentResponse, string>> =
    let tryGetFileCheckerOptionsWithLines file =
      x.TryGetFileCheckerOptionsWithLines file |> Result.map snd

    let formatSelectionAsync x = fantomasService.FormatSelectionAsync x
    Commands.formatSelection tryGetFileCheckerOptionsWithLines formatSelectionAsync file rangeToFormat

  member _.ClearFantomasCache() = fantomasService.ClearCache()

  /// gets the semantic classification ranges for a file, optionally filtered by a given range.
  member x.GetHighlighting(file: string<LocalPath>, range: Range option) =
    asyncOption {
      let! res = x.TryGetRecentTypeCheckResultsForFile file

      let r = res.GetCheckResults.GetSemanticClassification(range)

      let filteredRanges = Commands.scrubRanges r
      return CoreResponse.Res filteredRanges
    }

  member __.SetWorkspaceRoot(root: string option) = workspaceRoot <- root
  // linterConfiguration <- Lint.loadConfiguration workspaceRoot linterConfigFileRelativePath

  member __.SetLinterConfigRelativePath(relativePath: string option) =
    linterConfigFileRelativePath <- relativePath
  // linterConfiguration <- Lint.loadConfiguration workspaceRoot linterConfigFileRelativePath

  // member __.FSharpLiterate (file: string<LocalPath>) =
  //   async {
  //     let cnt =
  //       match state.TryGetFileSource file with
  //       | Ok ctn -> ctn.ToString()
  //       | _ ->  File.ReadAllText (UMX.untag file)
  //     let parsedFile =
  //       if Utils.isAScript (UMX.untag file) then
  //         FSharp.Formatting.Literate.Literate.ParseScriptString cnt
  //       else
  //          FSharp.Formatting.Literate.Literate.ParseMarkdownString cnt

  //     let html =  FSharp.Formatting.Literate.Literate.ToHtml parsedFile
  //     return CoreResponse.Res html
  //   }

  static member InlayHints(text, tyRes: ParseAndCheckResults, range, ?showTypeHints, ?showParameterHints) =
    let hintConfig: Core.InlayHints.HintConfig =
      { ShowTypeHints = defaultArg showTypeHints true
        ShowParameterHints = defaultArg showParameterHints true }

    FsAutoComplete.Core.InlayHints.provideHints (text, tyRes, range, hintConfig)

  member __.PipelineHints(tyRes: ParseAndCheckResults) =
    Commands.pipelineHints state.TryGetFileSource tyRes

  interface IDisposable with
    member x.Dispose() =
      for disposable in disposables do
        disposable.Dispose()
