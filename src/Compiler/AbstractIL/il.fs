// Copyright (c) Microsoft Corporation. All Rights Reserved. See License.txt in the project root for license information.

module FSharp.Compiler.AbstractIL.IL

open FSharp.Compiler.IO
open Internal.Utilities.Library

#nowarn "49"
#nowarn "343" // The type 'ILAssemblyRef' implements 'System.IComparable' explicitly but provides no corresponding override for 'Object.Equals'.
#nowarn "346" // The struct, record or union type 'IlxExtensionType' has an explicit implementation of 'Object.Equals'. ...

open System
open System.Diagnostics
open System.Collections
open System.Collections.Generic
open System.Collections.Concurrent
open System.Collections.ObjectModel
open System.Linq
open System.Reflection
open System.Text
open System.Threading

open FSharp.Compiler.AbstractIL.Diagnostics
open Internal.Utilities.Library
open Internal.Utilities

let logging = false

let _ =
    if logging then
        dprintn "* warning: Il.logging is on"

/// A little ugly, but the idea is that if a data structure does not
/// contain lazy values then we don't add laziness. So if the thing to map
/// is already evaluated then immediately apply the function.
let lazyMap f (x: InterruptibleLazy<_>) =
    if x.IsValueCreated then
        notlazy (f (x.Force()))
    else
        InterruptibleLazy(fun _ -> f (x.Force()))

[<RequireQualifiedAccess>]
type PrimaryAssembly =
    | Mscorlib
    | System_Runtime
    | NetStandard

    member this.Name =
        match this with
        | Mscorlib -> "mscorlib"
        | System_Runtime -> "System.Runtime"
        | NetStandard -> "netstandard"

    static member IsPossiblePrimaryAssembly(fileName: string) =
        let name = System.IO.Path.GetFileNameWithoutExtension(fileName)

        String.Equals(name, "System.Runtime", StringComparison.OrdinalIgnoreCase)
        || String.Equals(name, "mscorlib", StringComparison.OrdinalIgnoreCase)
        || String.Equals(name, "netstandard", StringComparison.OrdinalIgnoreCase)
        || String.Equals(name, "System.Private.CoreLib", StringComparison.OrdinalIgnoreCase)

// --------------------------------------------------------------------
// Utilities: type names
// --------------------------------------------------------------------

/// Global State. All namespace splits ever seen
// ++GLOBAL MUTABLE STATE (concurrency-safe)
let memoizeNamespaceTable = ConcurrentDictionary<string, string list>()

//  ++GLOBAL MUTABLE STATE (concurrency-safe)
let memoizeNamespaceRightTable =
    ConcurrentDictionary<string, string option * string>()

// ++GLOBAL MUTABLE STATE (concurrency-safe)
let memoizeNamespacePartTable = ConcurrentDictionary<string, string>()

let splitNameAt (nm: string) idx =
    if idx < 0 then
        failwith "splitNameAt: idx < 0"

    let last = nm.Length - 1

    if idx > last then
        failwith "splitNameAt: idx > last"

    (nm.Substring(0, idx)), (if idx < last then nm.Substring(idx + 1, last - idx) else "")

let rec splitNamespaceAux (nm: string) =
    match nm.IndexOf '.' with
    | -1 -> [ nm ]
    | idx ->
        let s1, s2 = splitNameAt nm idx
        let s1 = memoizeNamespacePartTable.GetOrAdd(s1, s1)
        s1 :: splitNamespaceAux s2

// Cache this as a delegate.
let splitNamespaceAuxDelegate = Func<string, string list> splitNamespaceAux

let splitNamespace nm =
    memoizeNamespaceTable.GetOrAdd(nm, splitNamespaceAuxDelegate)

// ++GLOBAL MUTABLE STATE (concurrency-safe)
let memoizeNamespaceArrayTable = ConcurrentDictionary<string, string[]>()

// Cache this as a delegate.
let splitNamespaceToArrayDelegate =
    Func<string, string array>(splitNamespace >> Array.ofList)

let splitNamespaceToArray nm =
    memoizeNamespaceArrayTable.GetOrAdd(nm, splitNamespaceToArrayDelegate)

let splitILTypeName (nm: string) =
    match nm.LastIndexOf '.' with
    | -1 -> [], nm
    | idx ->
        let s1, s2 = splitNameAt nm idx
        splitNamespace s1, s2

// Duplicate of comment in import.fs:
//   The type names that flow to the point include the "mangled" type names used for static parameters for provided types.
//   For example,
//       Foo.Bar,"1.0"
//   This is because the ImportSystemType code goes via Abstract IL type references. Ultimately this probably isn't
//   the best way to do things.
let splitILTypeNameWithPossibleStaticArguments (nm: string) =
    let nm, suffix =
        match nm.IndexOf ',' with
        | -1 -> nm, None
        | idx -> let s1, s2 = splitNameAt nm idx in s1, Some s2

    let nsp, nm =
        match nm.LastIndexOf '.' with
        | -1 -> [||], nm
        | idx ->
            let s1, s2 = splitNameAt nm idx
            splitNamespaceToArray s1, s2

    nsp,
    (match suffix with
     | None -> nm
     | Some s -> nm + "," + s)

(*
splitILTypeNameWithPossibleStaticArguments "Foo" = ([| |], "Foo")
splitILTypeNameWithPossibleStaticArguments "Foo.Bar" = ([| "Foo" |], "Bar")
splitILTypeNameWithPossibleStaticArguments "Foo.Bar,3" = ([| "Foo" |], "Bar, 3")
splitILTypeNameWithPossibleStaticArguments "Foo.Bar," = ([| "Foo" |], "Bar,")
splitILTypeNameWithPossibleStaticArguments "Foo.Bar,\"1.0\"" = ([| "Foo" |], "Bar,\"1.0\"")
splitILTypeNameWithPossibleStaticArguments "Foo.Bar.Bar,\"1.0\"" = ([| "Foo"; "Bar" |], "Bar,\"1.0\"")
*)

let splitTypeNameRightAux (nm: string) =
    let idx = nm.LastIndexOf '.'

    if idx = -1 then
        None, nm
    else
        let s1, s2 = splitNameAt nm idx
        Some s1, s2

// Cache this as a delegate.
let splitTypeNameRightAuxDelegate =
    Func<string, string option * string> splitTypeNameRightAux

let splitTypeNameRight nm =
    memoizeNamespaceRightTable.GetOrAdd(nm, splitTypeNameRightAuxDelegate)

// --------------------------------------------------------------------
// Ordered lists with a lookup table
// --------------------------------------------------------------------

/// This is used to store event, property and field maps.
type LazyOrderedMultiMap<'Key, 'Data when 'Key: equality and 'Key: not null>(keyf: 'Data -> 'Key, lazyItems: InterruptibleLazy<'Data list>)
    =

    let quickMap =
        lazyItems
        |> lazyMap (fun entries ->
            let t = Dictionary<_, _>(entries.Length, HashIdentity.Structural)

            for y in entries do
                let key = keyf y

                let v =
                    match t.TryGetValue key with
                    | true, v -> v
                    | _ -> []

                t[key] <- y :: v

            t)

    member _.Entries() = lazyItems.Force()

    member _.Add y =
        new LazyOrderedMultiMap<'Key, 'Data>(keyf, lazyItems |> lazyMap (fun x -> y :: x))

    member _.Filter f =
        new LazyOrderedMultiMap<'Key, 'Data>(keyf, lazyItems |> lazyMap (List.filter f))

    member _.Item
        with get x =
            match quickMap.Force().TryGetValue x with
            | true, v -> v
            | _ -> []

    override _.ToString() = "<table>"

//---------------------------------------------------------------------
// SHA1 hash-signing algorithm. Used to get the public key token from
// the public key.
//---------------------------------------------------------------------

let b0 n = (n &&& 0xFF)
let b1 n = ((n >>> 8) &&& 0xFF)
let b2 n = ((n >>> 16) &&& 0xFF)
let b3 n = ((n >>> 24) &&& 0xFF)

module SHA1 =

    let inline (>>>&) (x: int) (y: int) = int32 (uint32 x >>> y)

    let f (t, b, c, d) =
        if t < 20 then (b &&& c) ||| ((~~~b) &&& d)
        elif t < 40 then b ^^^ c ^^^ d
        elif t < 60 then (b &&& c) ||| (b &&& d) ||| (c &&& d)
        else b ^^^ c ^^^ d

    [<Literal>]
    let k0to19 = 0x5A827999

    [<Literal>]
    let k20to39 = 0x6ED9EBA1

    [<Literal>]
    let k40to59 = 0x8F1BBCDC

    [<Literal>]
    let k60to79 = 0xCA62C1D6

    let k t =
        if t < 20 then k0to19
        elif t < 40 then k20to39
        elif t < 60 then k40to59
        else k60to79

    type SHAStream =
        {
            stream: byte[]
            mutable pos: int
            mutable eof: bool
        }

    let rotLeft32 x n = (x <<< n) ||| (x >>>& (32 - n))

    // padding and length (in bits!) recorded at end
    let shaAfterEof sha =
        let n = sha.pos
        let len = sha.stream.Length

        if n = len then
            0x80
        else
            let padded_len = (((len + 9 + 63) / 64) * 64) - 8

            if n < padded_len - 8 then
                0x0
            elif (n &&& 63) = 56 then
                int32 ((int64 len * int64 8) >>> 56) &&& 0xff
            elif (n &&& 63) = 57 then
                int32 ((int64 len * int64 8) >>> 48) &&& 0xff
            elif (n &&& 63) = 58 then
                int32 ((int64 len * int64 8) >>> 40) &&& 0xff
            elif (n &&& 63) = 59 then
                int32 ((int64 len * int64 8) >>> 32) &&& 0xff
            elif (n &&& 63) = 60 then
                int32 ((int64 len * int64 8) >>> 24) &&& 0xff
            elif (n &&& 63) = 61 then
                int32 ((int64 len * int64 8) >>> 16) &&& 0xff
            elif (n &&& 63) = 62 then
                int32 ((int64 len * int64 8) >>> 8) &&& 0xff
            elif (n &&& 63) = 63 then
                (sha.eof <- true
                 int32 (int64 len * int64 8) &&& 0xff)
            else
                0x0

    let shaRead8 sha =
        let s = sha.stream

        let b =
            if sha.pos >= s.Length then
                shaAfterEof sha
            else
                int32 s[sha.pos]

        sha.pos <- sha.pos + 1
        b

    let shaRead32 sha =
        let b0 = shaRead8 sha
        let b1 = shaRead8 sha
        let b2 = shaRead8 sha
        let b3 = shaRead8 sha
        let res = (b0 <<< 24) ||| (b1 <<< 16) ||| (b2 <<< 8) ||| b3
        res

    let sha1Hash sha =
        let mutable h0 = 0x67452301
        let mutable h1 = 0xEFCDAB89
        let mutable h2 = 0x98BADCFE
        let mutable h3 = 0x10325476
        let mutable h4 = 0xC3D2E1F0
        let mutable a = 0
        let mutable b = 0
        let mutable c = 0
        let mutable d = 0
        let mutable e = 0
        let w = Array.create 80 0x00

        while (not sha.eof) do
            for i = 0 to 15 do
                w[i] <- shaRead32 sha

            for t = 16 to 79 do
                w[t] <- rotLeft32 (w[t - 3] ^^^ w[t - 8] ^^^ w[t - 14] ^^^ w[t - 16]) 1

            a <- h0
            b <- h1
            c <- h2
            d <- h3
            e <- h4

            for t = 0 to 79 do
                let temp = (rotLeft32 a 5) + f (t, b, c, d) + e + w[t] + k t
                e <- d
                d <- c
                c <- rotLeft32 b 30
                b <- a
                a <- temp

            h0 <- h0 + a
            h1 <- h1 + b
            h2 <- h2 + c
            h3 <- h3 + d
            h4 <- h4 + e

        h0, h1, h2, h3, h4

    let sha1HashBytes s =
        let _h0, _h1, _h2, h3, h4 = sha1Hash { stream = s; pos = 0; eof = false } // the result of the SHA algorithm is stored in registers 3 and 4
        Array.map byte [| b0 h4; b1 h4; b2 h4; b3 h4; b0 h3; b1 h3; b2 h3; b3 h3 |]

    let sha1HashInt64 s =
        let _h0, _h1, _h2, h3, h4 = sha1Hash { stream = s; pos = 0; eof = false } // the result of the SHA algorithm is stored in registers 3 and 4
        (int64 h3 <<< 32) ||| int64 h4

let sha1HashBytes s = SHA1.sha1HashBytes s
let sha1HashInt64 s = SHA1.sha1HashInt64 s

// --------------------------------------------------------------------
//
// --------------------------------------------------------------------

[<Struct>]
type ILVersionInfo =

    val Major: uint16
    val Minor: uint16
    val Build: uint16
    val Revision: uint16

    new(major, minor, build, revision) =
        {
            Major = major
            Minor = minor
            Build = build
            Revision = revision
        }

    /// For debugging
    override x.ToString() =
        sprintf "ILVersionInfo: %u %u %u %u" x.Major x.Minor x.Build x.Revision

type Locale = string

[<StructuralEquality; StructuralComparison>]
type PublicKey =

    | PublicKey of byte[]

    | PublicKeyToken of byte[]

    member x.IsKey =
        match x with
        | PublicKey _ -> true
        | _ -> false

    member x.IsKeyToken =
        match x with
        | PublicKeyToken _ -> true
        | _ -> false

    member x.Key =
        match x with
        | PublicKey b -> b
        | _ -> invalidOp "not a key"

    member x.KeyToken =
        match x with
        | PublicKeyToken b -> b
        | _ -> invalidOp "not a key token"

    member x.ToToken() =
        match x with
        | PublicKey bytes -> SHA1.sha1HashBytes bytes
        | PublicKeyToken token -> token

    static member KeyAsToken key =
        PublicKeyToken(PublicKey(key).ToToken())

[<StructuralEquality; StructuralComparison>]
type AssemblyRefData =
    {
        assemRefName: string
        assemRefHash: byte[] option
        assemRefPublicKeyInfo: PublicKey option
        assemRefRetargetable: bool
        assemRefVersion: ILVersionInfo option
        assemRefLocale: Locale option
    }

    override x.ToString() = x.assemRefName

/// Global state: table of all assembly references keyed by AssemblyRefData.
let AssemblyRefUniqueStampGenerator = UniqueStampGenerator<AssemblyRefData>()

[<Sealed>]
type ILAssemblyRef(data) =
    let pkToken key =
        match key with
        | Some(PublicKey bytes) -> Some(PublicKey(SHA1.sha1HashBytes bytes))
        | Some(PublicKeyToken token) -> Some(PublicKey token)
        | None -> None

    let uniqueStamp =
        AssemblyRefUniqueStampGenerator.Encode
            { data with
                assemRefPublicKeyInfo = pkToken data.assemRefPublicKeyInfo
            }

    let uniqueIgnoringVersionStamp =
        AssemblyRefUniqueStampGenerator.Encode
            { data with
                assemRefVersion = None
                assemRefPublicKeyInfo = pkToken data.assemRefPublicKeyInfo
            }

    member x.Name = data.assemRefName

    member x.Hash = data.assemRefHash

    member x.PublicKey = data.assemRefPublicKeyInfo

    member x.Retargetable = data.assemRefRetargetable

    member x.Version = data.assemRefVersion

    member x.Locale = data.assemRefLocale

    member x.UniqueStamp = uniqueStamp

    member x.UniqueIgnoringVersionStamp = uniqueIgnoringVersionStamp

    member x.EqualsIgnoringVersion(aref: ILAssemblyRef) =
        aref.UniqueIgnoringVersionStamp = uniqueIgnoringVersionStamp

    override x.GetHashCode() = uniqueStamp

    override x.Equals yobj =
        ((!!yobj :?> ILAssemblyRef).UniqueStamp = uniqueStamp)

    interface IComparable with
        override x.CompareTo yobj =
            compare (!!yobj :?> ILAssemblyRef).UniqueStamp uniqueStamp

    static member Create(name, hash, publicKey, retargetable, version, locale) =
        ILAssemblyRef
            {
                assemRefName = name
                assemRefHash = hash
                assemRefPublicKeyInfo = publicKey
                assemRefRetargetable = retargetable
                assemRefVersion = version
                assemRefLocale = locale
            }

    static member FromAssemblyName(aname: AssemblyName) =

        let locale = None

        let publicKey =
            match aname.GetPublicKey() with
            | null
            | [||] ->
                match aname.GetPublicKeyToken() with
                | null
                | [||] -> None
                | bytes -> Some(PublicKeyToken bytes)
            | bytes -> Some(PublicKey bytes)

        let version =
            match aname.Version with
            | null -> None
            | v -> Some(ILVersionInfo(uint16 v.Major, uint16 v.Minor, uint16 v.Build, uint16 v.Revision))

        let retargetable = aname.Flags = AssemblyNameFlags.Retargetable

        let name =
            match aname.Name with
            | null -> aname.FullName
            | name -> name

        ILAssemblyRef.Create(name, None, publicKey, retargetable, version, locale)

    member aref.QualifiedName =
        let b = StringBuilder(100)
        let add (s: string) = b.Append s |> ignore
        let addC (s: char) = b.Append s |> ignore
        add aref.Name

        match aref.Version with
        | None -> ()
        | Some version ->
            add ", Version="
            add (string (int version.Major))
            add "."
            add (string (int version.Minor))
            add "."
            add (string (int version.Build))
            add "."
            add (string (int version.Revision))
            add ", Culture="

            match aref.Locale with
            | None -> add "neutral"
            | Some b -> add b

            add ", PublicKeyToken="

            match aref.PublicKey with
            | None -> add "null"
            | Some pki ->
                let pkt = pki.ToToken()

                let convDigit digit =
                    let digitc =
                        if digit < 10 then
                            Convert.ToInt32 '0' + digit
                        else
                            Convert.ToInt32 'a' + (digit - 10)

                    Convert.ToChar digitc

                for i = 0 to pkt.Length - 1 do
                    let v = pkt[i]
                    addC (convDigit (int32 v / 16))
                    addC (convDigit (int32 v % 16))
            // retargetable can be true only for system assemblies that definitely have Version
            if aref.Retargetable then
                add ", Retargetable=Yes"

        b.ToString()

[<StructuralEquality; StructuralComparison>]
type ILModuleRef =
    {
        name: string
        hasMetadata: bool
        hash: byte[] option
    }

    static member Create(name, hasMetadata, hash) =
        {
            name = name
            hasMetadata = hasMetadata
            hash = hash
        }

    member x.Name = x.name

    member x.HasMetadata = x.hasMetadata

    member x.Hash = x.hash

    override x.ToString() = x.Name

[<StructuralEquality; StructuralComparison>]
[<RequireQualifiedAccess>]
type ILScopeRef =
    | Local
    | Module of ILModuleRef
    | Assembly of ILAssemblyRef
    | PrimaryAssembly

    member x.IsLocalRef =
        match x with
        | ILScopeRef.Local -> true
        | _ -> false

    member x.QualifiedName =
        match x with
        | ILScopeRef.Local -> ""
        | ILScopeRef.Module mref -> "module " + mref.Name
        | ILScopeRef.Assembly aref -> aref.QualifiedName
        | ILScopeRef.PrimaryAssembly -> ""

type ILArrayBound = int32 option

type ILArrayBounds = ILArrayBound * ILArrayBound

[<StructuralEquality; StructuralComparison>]
type ILArrayShape =

    | ILArrayShape of ILArrayBounds list (* lobound/size pairs *)

    member x.Rank = (let (ILArrayShape l) = x in l.Length)

    static member SingleDimensional = ILArrayShapeStatics.SingleDimensional

    static member FromRank n =
        if n = 1 then
            ILArrayShape.SingleDimensional
        else
            ILArrayShape(List.replicate n (Some 0, None))

and ILArrayShapeStatics() =

    static let singleDimensional = ILArrayShape [ (Some 0, None) ]

    static member SingleDimensional = singleDimensional

/// Calling conventions. These are used in method pointer types.
[<StructuralEquality; StructuralComparison; RequireQualifiedAccess>]
type ILArgConvention =
    | Default
    | CDecl
    | StdCall
    | ThisCall
    | FastCall
    | VarArg

[<StructuralEquality; StructuralComparison; RequireQualifiedAccess>]
type ILThisConvention =
    | Instance
    | InstanceExplicit
    | Static

[<StructuralEquality; StructuralComparison>]
type ILCallingConv =

    | Callconv of ILThisConvention * ILArgConvention

    member x.ThisConv = let (Callconv(a, _b)) = x in a

    member x.BasicConv = let (Callconv(_a, b)) = x in b

    member x.IsInstance =
        match x.ThisConv with
        | ILThisConvention.Instance -> true
        | _ -> false

    member x.IsInstanceExplicit =
        match x.ThisConv with
        | ILThisConvention.InstanceExplicit -> true
        | _ -> false

    member x.IsStatic =
        match x.ThisConv with
        | ILThisConvention.Static -> true
        | _ -> false

    static member Instance = ILCallingConvStatics.Instance

    static member Static = ILCallingConvStatics.Static

    override x.ToString() =
        if x.IsStatic then "static" else "instance"

/// Static storage to amortize the allocation of <c>ILCallingConv.Instance</c> and <c>ILCallingConv.Static</c>.
and ILCallingConvStatics() =

    static let instanceCallConv = Callconv(ILThisConvention.Instance, ILArgConvention.Default)

    static let staticCallConv = Callconv(ILThisConvention.Static, ILArgConvention.Default)

    static member Instance = instanceCallConv

    static member Static = staticCallConv

type ILBoxity =
    | AsObject
    | AsValue

// IL type references have a pre-computed hash code to enable quick lookup tables during binary generation.
[<CustomEquality; CustomComparison; StructuredFormatDisplay("{DebugText}")>]
type ILTypeRef =
    {
        trefScope: ILScopeRef
        trefEnclosing: string list
        trefName: string
        hashCode: int
        mutable asBoxedType: ILType
    }

    static member ComputeHash(scope, enclosing, name) =
        hash scope * 17 ^^^ (hash enclosing * 101 <<< 1) ^^^ (hash name * 47 <<< 2)

    static member Create(scope, enclosing, name) =
        let hashCode = ILTypeRef.ComputeHash(scope, enclosing, name)

        {
            trefScope = scope
            trefEnclosing = enclosing
            trefName = name
            hashCode = hashCode
            asBoxedType = Unchecked.defaultof<_>
        }

    member x.Scope = x.trefScope

    member x.Enclosing = x.trefEnclosing

    member x.Name = x.trefName

    member x.ApproxId = x.hashCode

    member x.AsBoxedType(tspec: ILTypeSpec) =
        if isNil tspec.tspecInst then
            let v = x.asBoxedType

            match box v with
            | null ->
                let r = ILType.Boxed tspec
                x.asBoxedType <- r
                r
            | _ -> v
        else
            ILType.Boxed tspec

    override x.GetHashCode() = x.hashCode

    override x.Equals yobj =
        let y = (!!yobj :?> ILTypeRef)

        (x.ApproxId = y.ApproxId)
        && (x.Scope = y.Scope)
        && (x.Name = y.Name)
        && (x.Enclosing = y.Enclosing)

    member x.EqualsWithPrimaryScopeRef(primaryScopeRef: ILScopeRef, yobj: obj) =
        let y = (yobj :?> ILTypeRef)

        let isPrimary (v: ILTypeRef) =
            match v.Scope with
            | ILScopeRef.PrimaryAssembly -> true
            | _ -> false

        // Since we can remap the scope, we need to recompute hash ... this is not an expensive operation
        let isPrimaryX = isPrimary x
        let isPrimaryY = isPrimary y

        let xApproxId =
            if isPrimaryX && not (isPrimaryY) then
                ILTypeRef.ComputeHash(primaryScopeRef, x.Enclosing, x.Name)
            else
                x.ApproxId

        let yApproxId =
            if isPrimaryY && not (isPrimaryX) then
                ILTypeRef.ComputeHash(primaryScopeRef, y.Enclosing, y.Name)
            else
                y.ApproxId

        let xScope = if isPrimaryX then primaryScopeRef else x.Scope

        let yScope = if isPrimaryY then primaryScopeRef else y.Scope

        (xApproxId = yApproxId)
        && (xScope = yScope)
        && (x.Name = y.Name)
        && (x.Enclosing = y.Enclosing)

    interface IComparable with

        override x.CompareTo yobj =
            let y = (!!yobj :?> ILTypeRef)
            let c = compare x.ApproxId y.ApproxId

            if c <> 0 then
                c
            else
                let c = compare x.Scope y.Scope

                if c <> 0 then
                    c
                else
                    let c = compare x.Name y.Name

                    if c <> 0 then c else compare x.Enclosing y.Enclosing

    member tref.FullName = String.concat "." (tref.Enclosing @ [ tref.Name ])

    member tref.BasicQualifiedName =
        (String.concat "+" (tref.Enclosing @ [ tref.Name ])).Replace(",", @"\,")

    member tref.AddQualifiedNameExtension basic =
        let sco = tref.Scope.QualifiedName

        if String.IsNullOrEmpty(sco) then
            basic
        else
            String.concat ", " [ basic; sco ]

    member tref.QualifiedName = tref.AddQualifiedNameExtension tref.BasicQualifiedName

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    /// For debugging
    override x.ToString() : string = x.FullName

and [<StructuralEquality; StructuralComparison; StructuredFormatDisplay("{DebugText}")>] ILTypeSpec =
    {
        tspecTypeRef: ILTypeRef
        /// The type instantiation if the type is generic.
        tspecInst: ILGenericArgs
    }

    member x.TypeRef = x.tspecTypeRef

    member x.Scope = x.TypeRef.Scope

    member x.Enclosing = x.TypeRef.Enclosing

    member x.Name = x.TypeRef.Name

    member x.GenericArgs = x.tspecInst

    static member Create(typeRef, instantiation) =
        {
            tspecTypeRef = typeRef
            tspecInst = instantiation
        }

    member x.BasicQualifiedName =
        let tc = x.TypeRef.BasicQualifiedName

        if isNil x.GenericArgs then
            tc
        else
            tc
            + "["
            + String.concat "," (x.GenericArgs |> List.map (fun arg -> "[" + arg.QualifiedName + "]"))
            + "]"

    member x.AddQualifiedNameExtension basic =
        x.TypeRef.AddQualifiedNameExtension basic

    member x.FullName = x.TypeRef.FullName

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    member x.EqualsWithPrimaryScopeRef(primaryScopeRef: ILScopeRef, yobj: obj) =
        let y = (yobj :?> ILTypeSpec)

        x.tspecTypeRef.EqualsWithPrimaryScopeRef(primaryScopeRef, y.TypeRef)
        && (x.GenericArgs = y.GenericArgs)

    override x.ToString() =
        x.TypeRef.FullName + if isNil x.GenericArgs then "" else "<...>"

and [<RequireQualifiedAccess; StructuralEquality; StructuralComparison; StructuredFormatDisplay("{DebugText}")>] ILType =
    | Void
    | Array of ILArrayShape * ILType
    | Value of ILTypeSpec
    | Boxed of ILTypeSpec
    | Ptr of ILType
    | Byref of ILType
    | FunctionPointer of ILCallingSignature
    | TypeVar of uint16
    | Modified of bool * ILTypeRef * ILType

    member x.BasicQualifiedName =
        match x with
        | ILType.TypeVar n -> "!" + string n
        | ILType.Modified(_, _ty1, ty2) -> ty2.BasicQualifiedName
        | ILType.Array(ILArrayShape s, ty) -> ty.BasicQualifiedName + "[" + String(',', s.Length - 1) + "]"
        | ILType.Value tr
        | ILType.Boxed tr -> tr.BasicQualifiedName
        | ILType.Void -> "void"
        | ILType.Ptr _ty -> failwith "unexpected pointer type"
        | ILType.Byref _ty -> failwith "unexpected byref type"
        | ILType.FunctionPointer _mref -> failwith "unexpected function pointer type"

    member x.AddQualifiedNameExtension basic =
        match x with
        | ILType.TypeVar _n -> basic
        | ILType.Modified(_, _ty1, ty2) -> ty2.AddQualifiedNameExtension basic
        | ILType.Array(ILArrayShape(_s), ty) -> ty.AddQualifiedNameExtension basic
        | ILType.Value tr
        | ILType.Boxed tr -> tr.AddQualifiedNameExtension basic
        | ILType.Void -> failwith "void"
        | ILType.Ptr _ty -> failwith "unexpected pointer type"
        | ILType.Byref _ty -> failwith "unexpected byref type"
        | ILType.FunctionPointer _mref -> failwith "unexpected function pointer type"

    member x.QualifiedName = x.AddQualifiedNameExtension(x.BasicQualifiedName)

    member x.TypeSpec =
        match x with
        | ILType.Boxed tr
        | ILType.Value tr -> tr
        | _ -> invalidOp "not a nominal type"

    member x.Boxity =
        match x with
        | ILType.Boxed _ -> AsObject
        | ILType.Value _ -> AsValue
        | _ -> invalidOp "not a nominal type"

    member x.TypeRef =
        match x with
        | ILType.Boxed tspec
        | ILType.Value tspec -> tspec.TypeRef
        | _ -> invalidOp "not a nominal type"

    member x.IsNominal =
        match x with
        | ILType.Boxed _
        | ILType.Value _ -> true
        | _ -> false

    member x.GenericArgs =
        match x with
        | ILType.Boxed tspec
        | ILType.Value tspec -> tspec.GenericArgs
        | _ -> []

    member x.IsTyvar =
        match x with
        | ILType.TypeVar _ -> true
        | _ -> false

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = x.QualifiedName

and [<StructuralEquality; StructuralComparison>] ILCallingSignature =
    {
        CallingConv: ILCallingConv
        ArgTypes: ILTypes
        ReturnType: ILType
    }

and ILGenericArgs = ILType list

and ILTypes = ILType list

let mkILCallSig (cc, args, ret) =
    {
        ArgTypes = args
        CallingConv = cc
        ReturnType = ret
    }

let mkILBoxedType (tspec: ILTypeSpec) = tspec.TypeRef.AsBoxedType tspec

[<StructuralEquality; StructuralComparison; StructuredFormatDisplay("{DebugText}")>]
type ILMethodRef =
    {
        mrefParent: ILTypeRef
        mrefCallconv: ILCallingConv
        mrefGenericArity: int
        mrefName: string
        mrefArgs: ILTypes
        mrefReturn: ILType
    }

    member x.DeclaringTypeRef = x.mrefParent

    member x.CallingConv = x.mrefCallconv

    member x.Name = x.mrefName

    member x.GenericArity = x.mrefGenericArity

    member x.ArgCount = List.length x.mrefArgs

    member x.ArgTypes = x.mrefArgs

    member x.ReturnType = x.mrefReturn

    member x.GetCallingSignature() =
        mkILCallSig (x.CallingConv, x.ArgTypes, x.ReturnType)

    static member Create(enclosingTypeRef, callingConv, name, genericArity, argTypes, returnType) =
        {
            mrefParent = enclosingTypeRef
            mrefCallconv = callingConv
            mrefName = name
            mrefGenericArity = genericArity
            mrefArgs = argTypes
            mrefReturn = returnType
        }

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    member x.FullName = x.DeclaringTypeRef.FullName + "::" + x.Name + "(...)"

    override x.ToString() = x.FullName

[<StructuralEquality; StructuralComparison; StructuredFormatDisplay("{DebugText}")>]
type ILFieldRef =
    {
        DeclaringTypeRef: ILTypeRef
        Name: string
        Type: ILType
    }

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() =
        x.DeclaringTypeRef.FullName + "::" + x.Name

[<StructuralEquality; StructuralComparison; StructuredFormatDisplay("{DebugText}")>]
type ILMethodSpec =
    {
        mspecMethodRef: ILMethodRef

        mspecDeclaringType: ILType

        mspecMethodInst: ILGenericArgs
    }

    static member Create(a, b, c) =
        {
            mspecDeclaringType = a
            mspecMethodRef = b
            mspecMethodInst = c
        }

    member x.MethodRef = x.mspecMethodRef

    member x.DeclaringType = x.mspecDeclaringType

    member x.GenericArgs = x.mspecMethodInst

    member x.Name = x.MethodRef.Name

    member x.CallingConv = x.MethodRef.CallingConv

    member x.GenericArity = x.MethodRef.GenericArity

    member x.FormalArgTypes = x.MethodRef.ArgTypes

    member x.FormalReturnType = x.MethodRef.ReturnType

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = x.MethodRef.FullName + "(...)"

[<StructuralEquality; StructuralComparison; StructuredFormatDisplay("{DebugText}")>]
type ILFieldSpec =
    {
        FieldRef: ILFieldRef
        DeclaringType: ILType
    }

    member x.FormalType = x.FieldRef.Type

    member x.Name = x.FieldRef.Name

    member x.DeclaringTypeRef = x.FieldRef.DeclaringTypeRef

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = x.FieldRef.ToString()

// --------------------------------------------------------------------
// Debug info.
// --------------------------------------------------------------------

type ILGuid = byte[]

type ILPlatform =
    | X86
    | AMD64
    | IA64
    | ARM
    | ARM64

type ILSourceDocument =
    {
        sourceLanguage: ILGuid option
        sourceVendor: ILGuid option
        sourceDocType: ILGuid option
        sourceFile: string
    }

    static member Create(language, vendor, documentType, file) =
        {
            sourceLanguage = language
            sourceVendor = vendor
            sourceDocType = documentType
            sourceFile = file
        }

    member x.Language = x.sourceLanguage

    member x.Vendor = x.sourceVendor

    member x.DocumentType = x.sourceDocType

    member x.File = x.sourceFile

    override x.ToString() = x.File

[<StructuralEquality; StructuralComparison; StructuredFormatDisplay("{DebugText}")>]
type ILDebugPoint =
    {
        sourceDocument: ILSourceDocument
        sourceLine: int
        sourceColumn: int
        sourceEndLine: int
        sourceEndColumn: int
    }

    static member Create(document, line, column, endLine, endColumn) =
        {
            sourceDocument = document
            sourceLine = line
            sourceColumn = column
            sourceEndLine = endLine
            sourceEndColumn = endColumn
        }

    member x.Document = x.sourceDocument

    member x.Line = x.sourceLine

    member x.Column = x.sourceColumn

    member x.EndLine = x.sourceEndLine

    member x.EndColumn = x.sourceEndColumn

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() =
        sprintf "(%d, %d)-(%d, %d)" x.Line x.Column x.EndLine x.EndColumn

type ILAttribElem =
    | String of string option
    | Bool of bool
    | Char of char
    | SByte of int8
    | Int16 of int16
    | Int32 of int32
    | Int64 of int64
    | Byte of uint8
    | UInt16 of uint16
    | UInt32 of uint32
    | UInt64 of uint64
    | Single of single
    | Double of double
    | Null
    | Type of ILType option
    | TypeRef of ILTypeRef option
    | Array of ILType * ILAttribElem list

type ILAttributeNamedArg = string * ILType * bool * ILAttribElem

[<RequireQualifiedAccess; StructuralEquality; StructuralComparison; StructuredFormatDisplay("{DebugText}")>]
type ILAttribute =
    | Encoded of method: ILMethodSpec * data: byte[] * elements: ILAttribElem list
    | Decoded of method: ILMethodSpec * fixedArgs: ILAttribElem list * namedArgs: ILAttributeNamedArg list

    member x.Method =
        match x with
        | Encoded(method, _, _)
        | Decoded(method, _, _) -> method

    member x.Elements =
        match x with
        | Encoded(_, _, elements) -> elements
        | Decoded(_, fixedArgs, namedArgs) -> fixedArgs @ (namedArgs |> List.map (fun (_, _, _, e) -> e))

    member x.WithMethod(method: ILMethodSpec) =
        match x with
        | Encoded(_, data, elements) -> Encoded(method, data, elements)
        | Decoded(_, fixedArgs, namedArgs) -> Decoded(method, fixedArgs, namedArgs)

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = x.Method.MethodRef.FullName

[<NoEquality; NoComparison; Struct>]
type ILAttributes(array: ILAttribute[]) =
    member _.AsArray() = array

    member _.AsList() = array |> Array.toList

    static member val internal Empty = ILAttributes([||])

[<NoEquality; NoComparison>]
type ILAttributesStored =

    /// Computed by ilread.fs based on metadata index
    | Reader of (int32 -> ILAttribute[])

    /// Already computed
    | Given of ILAttributes

    member x.GetCustomAttrs metadataIndex =
        match x with
        | Reader f -> ILAttributes(f metadataIndex)
        | Given attrs -> attrs

let emptyILCustomAttrs = ILAttributes [||]

let mkILCustomAttrsFromArray (attrs: ILAttribute[]) =
    if attrs.Length = 0 then
        emptyILCustomAttrs
    else
        ILAttributes attrs

let mkILCustomAttrs l =
    match l with
    | [] -> emptyILCustomAttrs
    | _ -> mkILCustomAttrsFromArray (List.toArray l)

let emptyILCustomAttrsStored = ILAttributesStored.Given emptyILCustomAttrs

let storeILCustomAttrs (attrs: ILAttributes) =
    if attrs.AsArray().Length = 0 then
        emptyILCustomAttrsStored
    else
        ILAttributesStored.Given attrs

let mkILCustomAttrsComputed f =
    ILAttributesStored.Reader(fun _ -> f ())

let mkILCustomAttrsReader f = ILAttributesStored.Reader f

type ILCodeLabel = int

// --------------------------------------------------------------------
// Instruction set.
// --------------------------------------------------------------------

type ILBasicType =
    | DT_R
    | DT_I1
    | DT_U1
    | DT_I2
    | DT_U2
    | DT_I4
    | DT_U4
    | DT_I8
    | DT_U8
    | DT_R4
    | DT_R8
    | DT_I
    | DT_U
    | DT_REF

[<StructuralEquality; StructuralComparison; RequireQualifiedAccess>]
type ILToken =
    | ILType of ILType
    | ILMethod of ILMethodSpec
    | ILField of ILFieldSpec

[<StructuralEquality; StructuralComparison; RequireQualifiedAccess>]
type ILConst =
    | I4 of int32
    | I8 of int64
    | R4 of single
    | R8 of double

type ILTailcall =
    | Tailcall
    | Normalcall

type ILAlignment =
    | Aligned
    | Unaligned1
    | Unaligned2
    | Unaligned4

type ILVolatility =
    | Volatile
    | Nonvolatile

type ILReadonly =
    | ReadonlyAddress
    | NormalAddress

type ILVarArgs = ILTypes option

[<StructuralEquality; StructuralComparison>]
type ILComparisonInstr =
    | BI_beq
    | BI_bge
    | BI_bge_un
    | BI_bgt
    | BI_bgt_un
    | BI_ble
    | BI_ble_un
    | BI_blt
    | BI_blt_un
    | BI_bne_un
    | BI_brfalse
    | BI_brtrue

[<StructuralEquality; NoComparison>]
type ILInstr =
    | AI_add
    | AI_add_ovf
    | AI_add_ovf_un
    | AI_and
    | AI_div
    | AI_div_un
    | AI_ceq
    | AI_cgt
    | AI_cgt_un
    | AI_clt
    | AI_clt_un
    | AI_conv of ILBasicType
    | AI_conv_ovf of ILBasicType
    | AI_conv_ovf_un of ILBasicType
    | AI_mul
    | AI_mul_ovf
    | AI_mul_ovf_un
    | AI_rem
    | AI_rem_un
    | AI_shl
    | AI_shr
    | AI_shr_un
    | AI_sub
    | AI_sub_ovf
    | AI_sub_ovf_un
    | AI_xor
    | AI_or
    | AI_neg
    | AI_not
    | AI_ldnull
    | AI_dup
    | AI_pop
    | AI_ckfinite
    | AI_nop
    | AI_ldc of ILBasicType * ILConst
    | I_ldarg of uint16
    | I_ldarga of uint16
    | I_ldind of ILAlignment * ILVolatility * ILBasicType
    | I_ldloc of uint16
    | I_ldloca of uint16
    | I_starg of uint16
    | I_stind of ILAlignment * ILVolatility * ILBasicType
    | I_stloc of uint16

    | I_br of ILCodeLabel
    | I_jmp of ILMethodSpec
    | I_brcmp of ILComparisonInstr * ILCodeLabel
    | I_switch of ILCodeLabel list
    | I_ret

    | I_call of ILTailcall * ILMethodSpec * ILVarArgs
    | I_callvirt of ILTailcall * ILMethodSpec * ILVarArgs
    | I_callconstraint of callvirt: bool * ILTailcall * ILType * ILMethodSpec * ILVarArgs
    | I_calli of ILTailcall * ILCallingSignature * ILVarArgs
    | I_ldftn of ILMethodSpec
    | I_newobj of ILMethodSpec * ILVarArgs

    | I_throw
    | I_endfinally
    | I_endfilter
    | I_leave of ILCodeLabel
    | I_rethrow

    | I_ldsfld of ILVolatility * ILFieldSpec
    | I_ldfld of ILAlignment * ILVolatility * ILFieldSpec
    | I_ldsflda of ILFieldSpec
    | I_ldflda of ILFieldSpec
    | I_stsfld of ILVolatility * ILFieldSpec
    | I_stfld of ILAlignment * ILVolatility * ILFieldSpec
    | I_ldstr of string
    | I_isinst of ILType
    | I_castclass of ILType
    | I_ldtoken of ILToken
    | I_ldvirtftn of ILMethodSpec

    | I_cpobj of ILType
    | I_initobj of ILType
    | I_ldobj of ILAlignment * ILVolatility * ILType
    | I_stobj of ILAlignment * ILVolatility * ILType
    | I_box of ILType
    | I_unbox of ILType
    | I_unbox_any of ILType
    | I_sizeof of ILType

    | I_ldelem of ILBasicType
    | I_stelem of ILBasicType
    | I_ldelema of ILReadonly * bool * ILArrayShape * ILType
    | I_ldelem_any of ILArrayShape * ILType
    | I_stelem_any of ILArrayShape * ILType
    | I_newarr of ILArrayShape * ILType
    | I_ldlen

    | I_mkrefany of ILType
    | I_refanytype
    | I_refanyval of ILType

    | I_break
    | I_seqpoint of ILDebugPoint

    | I_arglist

    | I_localloc
    | I_cpblk of ILAlignment * ILVolatility
    | I_initblk of ILAlignment * ILVolatility

    (* FOR EXTENSIONS, e.g. MS-ILX *)
    | EI_ilzero of ILType
    | EI_ldlen_multi of int32 * int32

[<RequireQualifiedAccess>]
type ILExceptionClause =
    | Finally of (ILCodeLabel * ILCodeLabel)
    | Fault of (ILCodeLabel * ILCodeLabel)
    | FilterCatch of filterRange: (ILCodeLabel * ILCodeLabel) * handlerRange: (ILCodeLabel * ILCodeLabel)
    | TypeCatch of ILType * (ILCodeLabel * ILCodeLabel)

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type ILExceptionSpec =
    {
        Range: ILCodeLabel * ILCodeLabel
        Clause: ILExceptionClause
    }

/// Indicates that a particular local variable has a particular source
/// language name within a given set of ranges. This does not effect local
/// variable numbering, which is global over the whole method.
[<RequireQualifiedAccess; NoEquality; NoComparison>]
type ILLocalDebugMapping = { LocalIndex: int; LocalName: string }

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type ILLocalDebugInfo =
    {
        Range: ILCodeLabel * ILCodeLabel
        DebugMappings: ILLocalDebugMapping list
    }

    override x.ToString() =
        let firstLabel, secondLabel = x.Range
        sprintf "%i-%i" firstLabel secondLabel

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type ILCode =
    {
        Labels: Dictionary<ILCodeLabel, int>
        Instrs: ILInstr[]
        Exceptions: ILExceptionSpec list
        Locals: ILLocalDebugInfo list
    }

    override x.ToString() = "<code>"

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type ILLocal =
    {
        Type: ILType
        IsPinned: bool
        DebugInfo: (string * int * int) option
    }

    override x.ToString() = "<local>"

type ILLocals = ILLocal list

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type ILDebugImport =
    | ImportType of targetType: ILType // * alias: string option
    | ImportNamespace of targetNamespace: string // * assembly: ILAssemblyRef option * alias: string option

//| ReferenceAlias of string
//| OpenXmlNamespace of prefix: string * xmlNamespace: string

type ILDebugImports =
    {
        Parent: ILDebugImports option
        Imports: ILDebugImport[]
    }

    override x.ToString() = "<imports>"

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type ILMethodBody =
    {
        IsZeroInit: bool
        MaxStack: int32
        NoInlining: bool
        AggressiveInlining: bool
        Locals: ILLocals
        Code: ILCode
        DebugRange: ILDebugPoint option
        DebugImports: ILDebugImports option
    }

    override x.ToString() = "<method body>"

[<RequireQualifiedAccess>]
type ILMemberAccess =
    | Assembly
    | CompilerControlled
    | FamilyAndAssembly
    | FamilyOrAssembly
    | Family
    | Private
    | Public

[<RequireQualifiedAccess; StructuralEquality; StructuralComparison>]
type ILFieldInit =
    | String of string
    | Bool of bool
    | Char of uint16
    | Int8 of int8
    | Int16 of int16
    | Int32 of int32
    | Int64 of int64
    | UInt8 of uint8
    | UInt16 of uint16
    | UInt32 of uint32
    | UInt64 of uint64
    | Single of single
    | Double of double
    | Null

    member x.AsObject() =
        match x with
        | ILFieldInit.String s -> box s
        | ILFieldInit.Bool bool -> box bool
        | ILFieldInit.Char u16 -> box (char (int u16))
        | ILFieldInit.Int8 i8 -> box i8
        | ILFieldInit.Int16 i16 -> box i16
        | ILFieldInit.Int32 i32 -> box i32
        | ILFieldInit.Int64 i64 -> box i64
        | ILFieldInit.UInt8 u8 -> box u8
        | ILFieldInit.UInt16 u16 -> box u16
        | ILFieldInit.UInt32 u32 -> box u32
        | ILFieldInit.UInt64 u64 -> box u64
        | ILFieldInit.Single ieee32 -> box ieee32
        | ILFieldInit.Double ieee64 -> box ieee64
        | ILFieldInit.Null -> (null :> objnull)

// --------------------------------------------------------------------
// Native Types, for marshalling to the native C interface.
// These are taken directly from the ILASM syntax, and don't really
// correspond yet to the CLI ECMA-335 Spec (Partition II, 7.4).
// --------------------------------------------------------------------

[<RequireQualifiedAccess; StructuralEquality; StructuralComparison>]
type ILNativeType =
    | Empty
    | Custom of ILGuid * nativeTypeName: string * custMarshallerName: string * cookieString: byte[]
    | FixedSysString of int32
    | FixedArray of int32
    | Currency
    | LPSTR
    | LPWSTR
    | LPTSTR
    | LPUTF8STR
    | ByValStr
    | TBSTR
    | LPSTRUCT
    | Struct
    | Void
    | Bool
    | Int8
    | Int16
    | Int32
    | Int64
    | Single
    | Double
    | Byte
    | UInt16
    | UInt32
    | UInt64
    | Array of
        ILNativeType option *
        (int32 * int32 option) option (* optional idx of parameter giving size plus optional additive i.e. num elems *)
    | Int
    | UInt
    | Method
    | AsAny
    | BSTR
    | IUnknown
    | IDispatch
    | Interface
    | Error
    | SafeArray of ILNativeVariant * string option
    | ANSIBSTR
    | VariantBool

and [<RequireQualifiedAccess; StructuralEquality; StructuralComparison>] ILNativeVariant =
    | Empty
    | Null
    | Variant
    | Currency
    | Decimal
    | Date
    | BSTR
    | LPSTR
    | LPWSTR
    | IUnknown
    | IDispatch
    | SafeArray
    | Error
    | HRESULT
    | CArray
    | UserDefined
    | Record
    | FileTime
    | Blob
    | Stream
    | Storage
    | StreamedObject
    | StoredObject
    | BlobObject
    | CF
    | CLSID
    | Void
    | Bool
    | Int8
    | Int16
    | Int32
    | Int64
    | Single
    | Double
    | UInt8
    | UInt16
    | UInt32
    | UInt64
    | PTR
    | Array of ILNativeVariant
    | Vector of ILNativeVariant
    | Byref of ILNativeVariant
    | Int
    | UInt

[<RequireQualifiedAccess; StructuralEquality; StructuralComparison>]
type ILSecurityAction =
    | Request
    | Demand
    | Assert
    | Deny
    | PermitOnly
    | LinkCheck
    | InheritCheck
    | ReqMin
    | ReqOpt
    | ReqRefuse
    | PreJitGrant
    | PreJitDeny
    | NonCasDemand
    | NonCasLinkDemand
    | NonCasInheritance
    | LinkDemandChoice
    | InheritanceDemandChoice
    | DemandChoice

[<RequireQualifiedAccess; StructuralEquality; StructuralComparison>]
type ILSecurityDecl = ILSecurityDecl of ILSecurityAction * byte[]

[<NoEquality; NoComparison; Struct>]
type ILSecurityDecls(array: ILSecurityDecl[]) =
    member x.AsArray() = array
    member x.AsList() = x.AsArray() |> Array.toList

[<NoEquality; NoComparison>]
type ILSecurityDeclsStored =

    /// Computed by ilread.fs based on metadata index
    | Reader of (int32 -> ILSecurityDecl[])

    /// Already computed
    | Given of ILSecurityDecls

    member x.GetSecurityDecls metadataIndex =
        match x with
        | Reader f -> ILSecurityDecls(f metadataIndex)
        | Given attrs -> attrs

let emptyILSecurityDecls = ILSecurityDecls [||]

let emptyILSecurityDeclsStored = ILSecurityDeclsStored.Given emptyILSecurityDecls

let mkILSecurityDecls l =
    match l with
    | [] -> emptyILSecurityDecls
    | _ -> ILSecurityDecls(Array.ofList l)

let storeILSecurityDecls (x: ILSecurityDecls) =
    if x.AsArray().Length = 0 then
        emptyILSecurityDeclsStored
    else
        ILSecurityDeclsStored.Given x

let mkILSecurityDeclsReader f = ILSecurityDeclsStored.Reader f

[<RequireQualifiedAccess>]
type PInvokeCharBestFit =
    | UseAssembly
    | Enabled
    | Disabled

[<RequireQualifiedAccess>]
type PInvokeThrowOnUnmappableChar =
    | UseAssembly
    | Enabled
    | Disabled

[<RequireQualifiedAccess>]
type PInvokeCallingConvention =
    | None
    | Cdecl
    | Stdcall
    | Thiscall
    | Fastcall
    | WinApi

[<RequireQualifiedAccess>]
type PInvokeCharEncoding =
    | None
    | Ansi
    | Unicode
    | Auto

[<RequireQualifiedAccess; NoComparison; NoEquality>]
type PInvokeMethod =
    {
        Where: ILModuleRef
        Name: string
        CallingConv: PInvokeCallingConvention
        CharEncoding: PInvokeCharEncoding
        NoMangle: bool
        LastError: bool
        ThrowOnUnmappableChar: PInvokeThrowOnUnmappableChar
        CharBestFit: PInvokeCharBestFit
    }

    override x.ToString() = x.Name

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type ILParameter =
    {
        Name: string option
        Type: ILType
        Default: ILFieldInit option
        Marshal: ILNativeType option
        IsIn: bool
        IsOut: bool
        IsOptional: bool
        CustomAttrsStored: ILAttributesStored
        MetadataIndex: int32
    }

    member x.CustomAttrs = x.CustomAttrsStored.GetCustomAttrs x.MetadataIndex

    override x.ToString() =
        x.Name |> Option.defaultValue "<no name>"

type ILParameters = ILParameter list

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type ILReturn =
    {
        Marshal: ILNativeType option
        Type: ILType
        CustomAttrsStored: ILAttributesStored
        MetadataIndex: int32
    }

    override x.ToString() = "<return>"

    member x.CustomAttrs = x.CustomAttrsStored.GetCustomAttrs x.MetadataIndex

    member x.WithCustomAttrs(customAttrs) =
        { x with
            CustomAttrsStored = storeILCustomAttrs customAttrs
        }

type ILOverridesSpec =
    | OverridesSpec of ILMethodRef * ILType

    member x.MethodRef = let (OverridesSpec(mr, _ty)) = x in mr

    member x.DeclaringType = let (OverridesSpec(_mr, ty)) = x in ty

    override x.ToString() =
        "overrides " + x.DeclaringType.ToString() + "::" + x.MethodRef.ToString()

type ILMethodVirtualInfo =
    {
        IsFinal: bool
        IsNewSlot: bool
        IsCheckAccessOnOverride: bool
        IsAbstract: bool
    }

[<RequireQualifiedAccess>]
type MethodBody =
    | IL of InterruptibleLazy<ILMethodBody>
    | PInvoke of Lazy<PInvokeMethod> (* platform invoke to native *)
    | Abstract
    | Native
    | NotAvailable

[<RequireQualifiedAccess>]
type MethodCodeKind =
    | IL
    | Native
    | Runtime

let typesOfILParams (ps: ILParameters) = ps |> List.map (fun p -> p.Type)

[<StructuralEquality; StructuralComparison>]
type ILGenericVariance =
    | NonVariant
    | CoVariant
    | ContraVariant

[<NoEquality; NoComparison; StructuredFormatDisplay("{DebugText}")>]
type ILGenericParameterDef =
    {
        Name: string
        Constraints: ILTypes
        Variance: ILGenericVariance
        HasReferenceTypeConstraint: bool
        HasNotNullableValueTypeConstraint: bool
        HasDefaultConstructorConstraint: bool
        HasAllowsRefStruct: bool
        CustomAttrsStored: ILAttributesStored
        MetadataIndex: int32
    }

    member x.CustomAttrs = x.CustomAttrsStored.GetCustomAttrs x.MetadataIndex

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = x.Name

type ILGenericParameterDefs = ILGenericParameterDef list

let memberAccessOfFlags flags =
    let f = (flags &&& 0x00000007)

    if f = 0x00000001 then ILMemberAccess.Private
    elif f = 0x00000006 then ILMemberAccess.Public
    elif f = 0x00000004 then ILMemberAccess.Family
    elif f = 0x00000002 then ILMemberAccess.FamilyAndAssembly
    elif f = 0x00000005 then ILMemberAccess.FamilyOrAssembly
    elif f = 0x00000003 then ILMemberAccess.Assembly
    else ILMemberAccess.CompilerControlled

let convertMemberAccess (ilMemberAccess: ILMemberAccess) =
    match ilMemberAccess with
    | ILMemberAccess.Public -> MethodAttributes.Public
    | ILMemberAccess.Private -> MethodAttributes.Private
    | ILMemberAccess.Assembly -> MethodAttributes.Assembly
    | ILMemberAccess.FamilyAndAssembly -> MethodAttributes.FamANDAssem
    | ILMemberAccess.CompilerControlled -> MethodAttributes.PrivateScope
    | ILMemberAccess.FamilyOrAssembly -> MethodAttributes.FamORAssem
    | ILMemberAccess.Family -> MethodAttributes.Family

let inline conditionalAdd condition flagToAdd source =
    if condition then
        source ||| flagToAdd
    else
        source &&& ~~~flagToAdd

let NoMetadataIdx = -1

type InterfaceImpl =
    {
        Idx: int
        Type: ILType
        mutable CustomAttrsStored: ILAttributesStored
    }

    member x.CustomAttrs =
        match x.CustomAttrsStored with
        | ILAttributesStored.Reader f ->
            let res = ILAttributes(f x.Idx)
            x.CustomAttrsStored <- ILAttributesStored.Given res
            res
        | ILAttributesStored.Given attrs -> attrs

    static member Create(ilType: ILType, customAttrsStored: ILAttributesStored) =
        {
            Idx = NoMetadataIdx
            Type = ilType
            CustomAttrsStored = customAttrsStored
        }

    static member Create(ilType: ILType) =
        InterfaceImpl.Create(ilType, emptyILCustomAttrsStored)

[<NoComparison; NoEquality; StructuredFormatDisplay("{DebugText}")>]
type ILMethodDef
    (
        name: string,
        attributes: MethodAttributes,
        implAttributes: MethodImplAttributes,
        callingConv: ILCallingConv,
        parameters: ILParameters,
        ret: ILReturn,
        body: InterruptibleLazy<MethodBody>,
        isEntryPoint: bool,
        genericParams: ILGenericParameterDefs,
        securityDeclsStored: ILSecurityDeclsStored,
        customAttrsStored: ILAttributesStored,
        metadataIndex: int32
    ) =

    new(name, attributes, implAttributes, callingConv, parameters, ret, body, isEntryPoint, genericParams, securityDecls, customAttrs) =
        ILMethodDef(
            name,
            attributes,
            implAttributes,
            callingConv,
            parameters,
            ret,
            body,
            isEntryPoint,
            genericParams,
            storeILSecurityDecls securityDecls,
            storeILCustomAttrs customAttrs,
            NoMetadataIdx
        )

    member private _.LazyBody = body

    // The captured data - remember the object will be as large as the data captured by these members
    member _.Name = name

    member _.Attributes = attributes

    member _.ImplAttributes = implAttributes

    member _.CallingConv = callingConv

    member _.Parameters = parameters

    member _.Return = ret

    member _.Body = body.Value

    member _.SecurityDeclsStored = securityDeclsStored

    member _.IsEntryPoint = isEntryPoint

    member _.GenericParams = genericParams

    member _.CustomAttrsStored = customAttrsStored

    member _.MetadataIndex = metadataIndex

    member x.With
        (
            ?name: string,
            ?attributes: MethodAttributes,
            ?implAttributes: MethodImplAttributes,
            ?callingConv: ILCallingConv,
            ?parameters: ILParameters,
            ?ret: ILReturn,
            ?body: InterruptibleLazy<MethodBody>,
            ?securityDecls: ILSecurityDecls,
            ?isEntryPoint: bool,
            ?genericParams: ILGenericParameterDefs,
            ?customAttrs: ILAttributes
        ) =

        ILMethodDef(
            name = defaultArg name x.Name,
            attributes = defaultArg attributes x.Attributes,
            implAttributes = defaultArg implAttributes x.ImplAttributes,
            callingConv = defaultArg callingConv x.CallingConv,
            parameters = defaultArg parameters x.Parameters,
            ret = defaultArg ret x.Return,
            body = defaultArg body x.LazyBody,
            securityDecls =
                (match securityDecls with
                 | None -> x.SecurityDecls
                 | Some attrs -> attrs),
            isEntryPoint = defaultArg isEntryPoint x.IsEntryPoint,
            genericParams = defaultArg genericParams x.GenericParams,
            customAttrs =
                (match customAttrs with
                 | None -> x.CustomAttrs
                 | Some attrs -> attrs)
        )

    member x.CustomAttrs = x.CustomAttrsStored.GetCustomAttrs metadataIndex

    member x.SecurityDecls = x.SecurityDeclsStored.GetSecurityDecls x.MetadataIndex

    member x.ParameterTypes = typesOfILParams x.Parameters

    member md.Code =
        match md.Body with
        | MethodBody.IL il -> Some il.Value.Code
        | _ -> None

    member x.IsIL =
        match x.Body with
        | MethodBody.IL _ -> true
        | _ -> false

    member x.Locals =
        match x.Body with
        | MethodBody.IL il -> il.Value.Locals
        | _ -> []

    member x.MethodBody =
        match x.Body with
        | MethodBody.IL il -> il.Value
        | _ -> failwith "not IL"

    member x.MaxStack = x.MethodBody.MaxStack

    member x.IsZeroInit = x.MethodBody.IsZeroInit

    member md.GetCallingSignature() =
        mkILCallSig (md.CallingConv, md.ParameterTypes, md.Return.Type)

    member x.IsClassInitializer = x.Name = ".cctor"

    member x.IsConstructor = x.Name = ".ctor"

    member x.Access = memberAccessOfFlags (int x.Attributes)

    member x.IsStatic = x.Attributes &&& MethodAttributes.Static <> enum 0

    member x.IsNonVirtualInstance = not x.IsStatic && not x.IsVirtual

    member x.IsVirtual = x.Attributes &&& MethodAttributes.Virtual <> enum 0

    member x.IsFinal = x.Attributes &&& MethodAttributes.Final <> enum 0

    member x.IsNewSlot = x.Attributes &&& MethodAttributes.NewSlot <> enum 0

    member x.IsCheckAccessOnOverride =
        x.Attributes &&& MethodAttributes.CheckAccessOnOverride <> enum 0

    member x.IsAbstract = x.Attributes &&& MethodAttributes.Abstract <> enum 0

    member x.IsHideBySig = x.Attributes &&& MethodAttributes.HideBySig <> enum 0

    member x.IsSpecialName = x.Attributes &&& MethodAttributes.SpecialName <> enum 0

    member x.IsUnmanagedExport =
        x.Attributes &&& MethodAttributes.UnmanagedExport <> enum 0

    member x.IsReqSecObj = x.Attributes &&& MethodAttributes.RequireSecObject <> enum 0

    member x.HasSecurity = x.Attributes &&& MethodAttributes.HasSecurity <> enum 0

    member x.IsManaged = x.ImplAttributes &&& MethodImplAttributes.Managed <> enum 0

    member x.IsForwardRef = x.ImplAttributes &&& MethodImplAttributes.ForwardRef <> enum 0

    member x.IsInternalCall =
        x.ImplAttributes &&& MethodImplAttributes.InternalCall <> enum 0

    member x.IsPreserveSig =
        x.ImplAttributes &&& MethodImplAttributes.PreserveSig <> enum 0

    member x.IsSynchronized =
        x.ImplAttributes &&& MethodImplAttributes.Synchronized <> enum 0

    member x.IsNoInline = x.ImplAttributes &&& MethodImplAttributes.NoInlining <> enum 0

    member x.IsAggressiveInline =
        x.ImplAttributes &&& MethodImplAttributes.AggressiveInlining <> enum 0

    member x.IsMustRun = x.ImplAttributes &&& MethodImplAttributes.NoOptimization <> enum 0

    member x.WithSpecialName =
        x.With(attributes = (x.Attributes ||| MethodAttributes.SpecialName))

    member x.WithHideBySig() =
        x.With(
            attributes =
                (if x.IsVirtual then
                     x.Attributes &&& ~~~MethodAttributes.CheckAccessOnOverride
                     ||| MethodAttributes.HideBySig
                 else
                     failwith "WithHideBySig")
        )

    member x.WithHideBySig(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition MethodAttributes.HideBySig))

    member x.WithFinal(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition MethodAttributes.Final))

    member x.WithAbstract(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition MethodAttributes.Abstract))

    member x.WithVirtual(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition MethodAttributes.Virtual))

    member x.WithAccess(access) =
        x.With(
            attributes =
                (x.Attributes &&& ~~~MethodAttributes.MemberAccessMask
                 ||| convertMemberAccess access)
        )

    member x.WithNewSlot = x.With(attributes = (x.Attributes ||| MethodAttributes.NewSlot))

    member x.WithSecurity(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition MethodAttributes.HasSecurity))

    member x.WithPInvoke(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition MethodAttributes.PinvokeImpl))

    member x.WithPreserveSig(condition) =
        x.With(implAttributes = (x.ImplAttributes |> conditionalAdd condition MethodImplAttributes.PreserveSig))

    member x.WithSynchronized(condition) =
        x.With(implAttributes = (x.ImplAttributes |> conditionalAdd condition MethodImplAttributes.Synchronized))

    member x.WithNoInlining(condition) =
        x.With(implAttributes = (x.ImplAttributes |> conditionalAdd condition MethodImplAttributes.NoInlining))

    member x.WithAggressiveInlining(condition) =
        x.With(
            implAttributes =
                (x.ImplAttributes
                 |> conditionalAdd condition MethodImplAttributes.AggressiveInlining)
        )

    member x.WithRuntime(condition) =
        x.With(implAttributes = (x.ImplAttributes |> conditionalAdd condition MethodImplAttributes.Runtime))

    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = "method " + x.Name

/// Index table by name and arity.
type MethodDefMap = Map<string, ILMethodDef list>

[<Sealed>]
type ILMethodDefs(f) =
    inherit DelayInitArrayMap<ILMethodDef, string, ILMethodDef list>(f)

    override this.CreateDictionary(arr) =
        let t = Dictionary(arr.Length)

        for i = arr.Length - 1 downto 0 do
            let y = arr[i]
            let key = y.Name

            match t.TryGetValue key with
            | true, m -> t[key] <- y :: m
            | _ -> t[key] <- [ y ]

        t

    interface IEnumerable with
        member x.GetEnumerator() =
            ((x :> IEnumerable<ILMethodDef>).GetEnumerator() :> IEnumerator)

    interface IEnumerable<ILMethodDef> with
        member x.GetEnumerator() =
            (x.GetArray() :> IEnumerable<ILMethodDef>).GetEnumerator()

    member x.AsArray() = x.GetArray()
    member x.AsList() = x.GetArray() |> Array.toList

    member x.FindByName nm =
        match x.GetDictionary().TryGetValue nm with
        | true, m -> m
        | _ -> []

    member x.FindByNameAndArity(nm, arity) =
        x.FindByName nm |> List.filter (fun x -> List.length x.Parameters = arity)

    member x.TryFindInstanceByNameAndCallingSignature(nm, callingSig) =
        x.FindByName nm
        |> List.tryFind (fun x -> not x.IsStatic && x.GetCallingSignature() = callingSig)

[<NoComparison; NoEquality; StructuredFormatDisplay("{DebugText}")>]
type ILEventDef
    (
        eventType: ILType option,
        name: string,
        attributes: EventAttributes,
        addMethod: ILMethodRef,
        removeMethod: ILMethodRef,
        fireMethod: ILMethodRef option,
        otherMethods: ILMethodRef list,
        customAttrsStored: ILAttributesStored,
        metadataIndex: int32
    ) =

    new(eventType, name, attributes, addMethod, removeMethod, fireMethod, otherMethods, customAttrs) =
        ILEventDef(
            eventType,
            name,
            attributes,
            addMethod,
            removeMethod,
            fireMethod,
            otherMethods,
            storeILCustomAttrs customAttrs,
            NoMetadataIdx
        )

    member _.EventType = eventType

    member _.Name = name

    member _.Attributes = attributes

    member _.AddMethod = addMethod

    member _.RemoveMethod = removeMethod

    member _.FireMethod = fireMethod

    member _.OtherMethods = otherMethods

    member _.CustomAttrsStored = customAttrsStored

    member _.MetadataIndex = metadataIndex

    member x.CustomAttrs = customAttrsStored.GetCustomAttrs x.MetadataIndex

    member x.With(?eventType, ?name, ?attributes, ?addMethod, ?removeMethod, ?fireMethod, ?otherMethods, ?customAttrs) =
        ILEventDef(
            eventType = defaultArg eventType x.EventType,
            name = defaultArg name x.Name,
            attributes = defaultArg attributes x.Attributes,
            addMethod = defaultArg addMethod x.AddMethod,
            removeMethod = defaultArg removeMethod x.RemoveMethod,
            fireMethod = defaultArg fireMethod x.FireMethod,
            otherMethods = defaultArg otherMethods x.OtherMethods,
            customAttrs =
                (match customAttrs with
                 | None -> x.CustomAttrs
                 | Some attrs -> attrs)
        )

    member x.IsSpecialName = (x.Attributes &&& EventAttributes.SpecialName) <> enum<_> (0)

    member x.IsRTSpecialName =
        (x.Attributes &&& EventAttributes.RTSpecialName) <> enum<_> (0)

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = "event " + x.Name

[<NoEquality; NoComparison>]
type ILEventDefs =
    | ILEvents of LazyOrderedMultiMap<string, ILEventDef>

    member x.AsList() = let (ILEvents t) = x in t.Entries()

    member x.LookupByName s = let (ILEvents t) = x in t[s]

    override x.ToString() = "<events>"

[<NoComparison; NoEquality; StructuredFormatDisplay("{DebugText}")>]
type ILPropertyDef
    (
        name: string,
        attributes: PropertyAttributes,
        setMethod: ILMethodRef option,
        getMethod: ILMethodRef option,
        callingConv: ILThisConvention,
        propertyType: ILType,
        init: ILFieldInit option,
        args: ILTypes,
        customAttrsStored: ILAttributesStored,
        metadataIndex: int32
    ) =

    new(name, attributes, setMethod, getMethod, callingConv, propertyType, init, args, customAttrs) =
        ILPropertyDef(
            name,
            attributes,
            setMethod,
            getMethod,
            callingConv,
            propertyType,
            init,
            args,
            storeILCustomAttrs customAttrs,
            NoMetadataIdx
        )

    member x.Name = name
    member x.Attributes = attributes
    member x.GetMethod = getMethod
    member x.SetMethod = setMethod
    member x.CallingConv = callingConv
    member x.PropertyType = propertyType
    member x.Init = init
    member x.Args = args
    member x.CustomAttrsStored = customAttrsStored
    member x.CustomAttrs = customAttrsStored.GetCustomAttrs x.MetadataIndex
    member x.MetadataIndex = metadataIndex

    member x.With(?name, ?attributes, ?setMethod, ?getMethod, ?callingConv, ?propertyType, ?init, ?args, ?customAttrs) =
        ILPropertyDef(
            name = defaultArg name x.Name,
            attributes = defaultArg attributes x.Attributes,
            setMethod = defaultArg setMethod x.SetMethod,
            getMethod = defaultArg getMethod x.GetMethod,
            callingConv = defaultArg callingConv x.CallingConv,
            propertyType = defaultArg propertyType x.PropertyType,
            init = defaultArg init x.Init,
            args = defaultArg args x.Args,
            customAttrs =
                (match customAttrs with
                 | None -> x.CustomAttrs
                 | Some attrs -> attrs)
        )

    member x.IsSpecialName =
        (x.Attributes &&& PropertyAttributes.SpecialName) <> enum<_> (0)

    member x.IsRTSpecialName =
        (x.Attributes &&& PropertyAttributes.RTSpecialName) <> enum<_> (0)

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = "property " + x.Name

// Index table by name.
[<NoEquality; NoComparison>]
type ILPropertyDefs =
    | ILProperties of LazyOrderedMultiMap<string, ILPropertyDef>

    member x.AsList() = let (ILProperties t) = x in t.Entries()

    member x.LookupByName s = let (ILProperties t) = x in t[s]

    override x.ToString() = "<properties>"

let convertFieldAccess (ilMemberAccess: ILMemberAccess) =
    match ilMemberAccess with
    | ILMemberAccess.Assembly -> FieldAttributes.Assembly
    | ILMemberAccess.CompilerControlled -> enum<FieldAttributes> (0)
    | ILMemberAccess.FamilyAndAssembly -> FieldAttributes.FamANDAssem
    | ILMemberAccess.FamilyOrAssembly -> FieldAttributes.FamORAssem
    | ILMemberAccess.Family -> FieldAttributes.Family
    | ILMemberAccess.Private -> FieldAttributes.Private
    | ILMemberAccess.Public -> FieldAttributes.Public

[<NoComparison; NoEquality; StructuredFormatDisplay("{DebugText}")>]
type ILFieldDef
    (
        name: string,
        fieldType: ILType,
        attributes: FieldAttributes,
        data: byte[] option,
        literalValue: ILFieldInit option,
        offset: int32 option,
        marshal: ILNativeType option,
        customAttrsStored: ILAttributesStored,
        metadataIndex: int32
    ) =

    new(name, fieldType, attributes, data, literalValue, offset, marshal, customAttrs) =
        ILFieldDef(name, fieldType, attributes, data, literalValue, offset, marshal, storeILCustomAttrs customAttrs, NoMetadataIdx)

    member _.Name = name
    member _.FieldType = fieldType
    member _.Attributes = attributes
    member _.Data = data
    member _.LiteralValue = literalValue
    member _.Offset = offset
    member _.Marshal = marshal
    member x.CustomAttrsStored = customAttrsStored
    member x.CustomAttrs = customAttrsStored.GetCustomAttrs x.MetadataIndex
    member x.MetadataIndex = metadataIndex

    member x.With
        (
            ?name: string,
            ?fieldType: ILType,
            ?attributes: FieldAttributes,
            ?data: byte[] option,
            ?literalValue: ILFieldInit option,
            ?offset: int32 option,
            ?marshal: ILNativeType option,
            ?customAttrs: ILAttributes
        ) =
        ILFieldDef(
            name = defaultArg name x.Name,
            fieldType = defaultArg fieldType x.FieldType,
            attributes = defaultArg attributes x.Attributes,
            data = defaultArg data x.Data,
            literalValue = defaultArg literalValue x.LiteralValue,
            offset = defaultArg offset x.Offset,
            marshal = defaultArg marshal x.Marshal,
            customAttrs = defaultArg customAttrs x.CustomAttrs
        )

    member x.IsStatic = x.Attributes &&& FieldAttributes.Static <> enum 0
    member x.IsSpecialName = x.Attributes &&& FieldAttributes.SpecialName <> enum 0
    member x.IsLiteral = x.Attributes &&& FieldAttributes.Literal <> enum 0
    member x.NotSerialized = x.Attributes &&& FieldAttributes.NotSerialized <> enum 0
    member x.IsInitOnly = x.Attributes &&& FieldAttributes.InitOnly <> enum 0
    member x.Access = memberAccessOfFlags (int x.Attributes)

    member x.WithAccess(access) =
        x.With(
            attributes =
                (x.Attributes &&& ~~~FieldAttributes.FieldAccessMask
                 ||| convertFieldAccess access)
        )

    member x.WithInitOnly(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition FieldAttributes.InitOnly))

    member x.WithStatic(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition FieldAttributes.Static))

    member x.WithSpecialName(condition) =
        x.With(
            attributes =
                (x.Attributes
                 |> conditionalAdd condition (FieldAttributes.SpecialName ||| FieldAttributes.RTSpecialName))
        )

    member x.WithNotSerialized(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition FieldAttributes.NotSerialized))

    member x.WithLiteralDefaultValue(literal) =
        x.With(
            literalValue = literal,
            attributes =
                (x.Attributes
                 |> conditionalAdd literal.IsSome (FieldAttributes.Literal ||| FieldAttributes.HasDefault))
        )

    member x.WithFieldMarshal(marshal) =
        x.With(marshal = marshal, attributes = (x.Attributes |> conditionalAdd marshal.IsSome FieldAttributes.HasFieldMarshal))

    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = "field " + x.Name

// Index table by name. Keep a canonical list to make sure field order is not disturbed for binary manipulation.
type ILFieldDefs =
    | ILFields of LazyOrderedMultiMap<string, ILFieldDef>

    member x.AsList() = let (ILFields t) = x in t.Entries()

    member x.LookupByName s = let (ILFields t) = x in t[s]

    override x.ToString() = "<fields>"

type ILMethodImplDef =
    {
        Overrides: ILOverridesSpec
        OverrideBy: ILMethodSpec
    }

// Index table by name and arity.
type ILMethodImplDefs =
    | ILMethodImpls of InterruptibleLazy<MethodImplsMap>

    member x.AsList() =
        let (ILMethodImpls ltab) = x in Map.foldBack (fun _x y r -> y @ r) (ltab.Force()) []

and MethodImplsMap = Map<string * int, ILMethodImplDef list>

[<RequireQualifiedAccess>]
type ILTypeDefLayout =
    | Auto
    | Sequential of ILTypeDefLayoutInfo
    | Explicit of ILTypeDefLayoutInfo (* REVIEW: add field info here *)

and ILTypeDefLayoutInfo =
    {
        Size: int32 option
        Pack: uint16 option
    }

[<RequireQualifiedAccess>]
type ILTypeInit =
    | BeforeField
    | OnAny

[<RequireQualifiedAccess>]
type ILDefaultPInvokeEncoding =
    | Ansi
    | Auto
    | Unicode

type ILTypeDefAccess =
    | Public
    | Private
    | Nested of ILMemberAccess

let typeAccessOfFlags flags =
    let f = (flags &&& 0x00000007)

    if f = 0x00000001 then
        ILTypeDefAccess.Public
    elif f = 0x00000002 then
        ILTypeDefAccess.Nested ILMemberAccess.Public
    elif f = 0x00000003 then
        ILTypeDefAccess.Nested ILMemberAccess.Private
    elif f = 0x00000004 then
        ILTypeDefAccess.Nested ILMemberAccess.Family
    elif f = 0x00000006 then
        ILTypeDefAccess.Nested ILMemberAccess.FamilyAndAssembly
    elif f = 0x00000007 then
        ILTypeDefAccess.Nested ILMemberAccess.FamilyOrAssembly
    elif f = 0x00000005 then
        ILTypeDefAccess.Nested ILMemberAccess.Assembly
    else
        ILTypeDefAccess.Private

let typeEncodingOfFlags flags =
    let f = (flags &&& 0x00030000)

    if f = 0x00020000 then ILDefaultPInvokeEncoding.Auto
    elif f = 0x00010000 then ILDefaultPInvokeEncoding.Unicode
    else ILDefaultPInvokeEncoding.Ansi

[<Flags>]
type ILTypeDefAdditionalFlags =
    | Class = 1
    | ValueType = 2
    | Interface = 4
    | Enum = 8
    | Delegate = 16
    | IsKnownToBeAttribute = 32
    /// The type can contain extension methods,
    /// or this information may not be available at the time the ILTypeDef is created
    | CanContainExtensionMethods = 1024

let internal typeKindFlags =
    ILTypeDefAdditionalFlags.Class
    ||| ILTypeDefAdditionalFlags.ValueType
    ||| ILTypeDefAdditionalFlags.Interface
    ||| ILTypeDefAdditionalFlags.Enum
    ||| ILTypeDefAdditionalFlags.Delegate

let inline internal resetTypeKind flags = flags &&& ~~~typeKindFlags

let (|HasFlag|_|) (flag: ILTypeDefAdditionalFlags) flags = flags &&& flag = flag

let inline typeKindByNames extendsName typeName =
    match extendsName with
    | "System.Enum" -> ILTypeDefAdditionalFlags.Enum
    | "System.Delegate" when typeName <> "System.MulticastDelegate" -> ILTypeDefAdditionalFlags.Delegate
    | "System.MulticastDelegate" -> ILTypeDefAdditionalFlags.Delegate
    | "System.ValueType" when typeName <> "System.Enum" -> ILTypeDefAdditionalFlags.ValueType
    | _ -> ILTypeDefAdditionalFlags.Class

let typeKindOfFlags nm (super: ILType option) flags =
    if (flags &&& 0x00000020) <> 0x0 then
        ILTypeDefAdditionalFlags.Interface
    else
        match super with
        | None -> ILTypeDefAdditionalFlags.Class
        | Some ty ->
            let name = ty.TypeSpec.Name
            typeKindByNames name nm

let convertTypeAccessFlags access =
    match access with
    | ILTypeDefAccess.Public -> TypeAttributes.Public
    | ILTypeDefAccess.Private -> TypeAttributes.NotPublic
    | ILTypeDefAccess.Nested ILMemberAccess.Public -> TypeAttributes.NestedPublic
    | ILTypeDefAccess.Nested ILMemberAccess.Private -> TypeAttributes.NestedPrivate
    | ILTypeDefAccess.Nested ILMemberAccess.Family -> TypeAttributes.NestedFamily
    | ILTypeDefAccess.Nested ILMemberAccess.CompilerControlled -> TypeAttributes.NestedPrivate
    | ILTypeDefAccess.Nested ILMemberAccess.FamilyAndAssembly -> TypeAttributes.NestedFamANDAssem
    | ILTypeDefAccess.Nested ILMemberAccess.FamilyOrAssembly -> TypeAttributes.NestedFamORAssem
    | ILTypeDefAccess.Nested ILMemberAccess.Assembly -> TypeAttributes.NestedAssembly

let convertTypeKind kind =
    match kind with
    | HasFlag ILTypeDefAdditionalFlags.Interface -> TypeAttributes.Abstract ||| TypeAttributes.Interface
    | _ -> TypeAttributes.Class

let convertLayout layout =
    match layout with
    | ILTypeDefLayout.Auto -> TypeAttributes.AutoLayout
    | ILTypeDefLayout.Sequential _ -> TypeAttributes.SequentialLayout
    | ILTypeDefLayout.Explicit _ -> TypeAttributes.ExplicitLayout

let convertEncoding encoding =
    match encoding with
    | ILDefaultPInvokeEncoding.Auto -> TypeAttributes.AutoClass
    | ILDefaultPInvokeEncoding.Ansi -> TypeAttributes.AnsiClass
    | ILDefaultPInvokeEncoding.Unicode -> TypeAttributes.UnicodeClass

let convertToNestedTypeAccess (ilMemberAccess: ILMemberAccess) =
    match ilMemberAccess with
    | ILMemberAccess.Assembly -> TypeAttributes.NestedAssembly
    | ILMemberAccess.CompilerControlled -> failwith "Method access compiler controlled."
    | ILMemberAccess.FamilyAndAssembly -> TypeAttributes.NestedFamANDAssem
    | ILMemberAccess.FamilyOrAssembly -> TypeAttributes.NestedFamORAssem
    | ILMemberAccess.Family -> TypeAttributes.NestedFamily
    | ILMemberAccess.Private -> TypeAttributes.NestedPrivate
    | ILMemberAccess.Public -> TypeAttributes.NestedPublic

let convertInitSemantics (init: ILTypeInit) =
    match init with
    | ILTypeInit.BeforeField -> TypeAttributes.BeforeFieldInit
    | ILTypeInit.OnAny -> enum 0

let emptyILExtends = notlazy<ILType option> None

[<NoComparison; NoEquality; StructuredFormatDisplay("{DebugText}")>]
type ILTypeDef
    (
        name: string,
        attributes: TypeAttributes,
        layout: ILTypeDefLayout,
        implements: InterruptibleLazy<InterfaceImpl list>,
        genericParams: ILGenericParameterDefs,
        extends: InterruptibleLazy<ILType option>,
        methods: ILMethodDefs,
        nestedTypes: ILTypeDefs,
        fields: ILFieldDefs,
        methodImpls: ILMethodImplDefs,
        events: ILEventDefs,
        properties: ILPropertyDefs,
        additionalFlags: ILTypeDefAdditionalFlags,
        securityDeclsStored: ILSecurityDeclsStored,
        customAttrsStored: ILAttributesStored,
        metadataIndex: int32
    ) =

    let mutable customAttrsStored = customAttrsStored

    let hasFlag flag = additionalFlags &&& flag = flag

    new
        (
            name,
            attributes,
            layout,
            implements,
            genericParams,
            extends,
            methods,
            nestedTypes,
            fields,
            methodImpls,
            events,
            properties,
            additionalFlags,
            securityDecls,
            customAttrs
        ) =
        ILTypeDef(
            name,
            attributes,
            layout,
            implements,
            genericParams,
            extends,
            methods,
            nestedTypes,
            fields,
            methodImpls,
            events,
            properties,
            additionalFlags,
            storeILSecurityDecls securityDecls,
            customAttrs,
            NoMetadataIdx
        )

    new
        (
            name,
            attributes,
            layout,
            implements,
            genericParams,
            extends,
            methods,
            nestedTypes,
            fields,
            methodImpls,
            events,
            properties,
            securityDecls,
            customAttrs
        ) =
        let additionalFlags =
            ILTypeDefAdditionalFlags.CanContainExtensionMethods
            ||| typeKindOfFlags name extends (int attributes)

        ILTypeDef(
            name,
            attributes,
            layout,
            InterruptibleLazy.FromValue(implements),
            genericParams,
            InterruptibleLazy.FromValue(extends),
            methods,
            nestedTypes,
            fields,
            methodImpls,
            events,
            properties,
            additionalFlags,
            storeILSecurityDecls securityDecls,
            customAttrs,
            NoMetadataIdx
        )

    member _.Name = name

    member _.Attributes = attributes

    member _.GenericParams = genericParams

    member _.Layout = layout

    member _.NestedTypes = nestedTypes

    member _.Implements = implements

    member _.Extends = extends

    member _.Methods = methods

    member _.SecurityDeclsStored = securityDeclsStored

    member _.Fields = fields

    member _.MethodImpls = methodImpls

    member _.Events = events

    member _.Properties = properties

    member _.IsKnownToBeAttribute = hasFlag ILTypeDefAdditionalFlags.IsKnownToBeAttribute

    member _.CanContainExtensionMethods =
        hasFlag ILTypeDefAdditionalFlags.CanContainExtensionMethods

    member _.CustomAttrsStored = customAttrsStored

    member _.MetadataIndex = metadataIndex

    member x.With
        (
            ?name,
            ?attributes,
            ?layout,
            ?implements,
            ?genericParams,
            ?extends,
            ?methods,
            ?nestedTypes,
            ?fields,
            ?methodImpls,
            ?events,
            ?properties,
            ?newAdditionalFlags,
            ?customAttrs,
            ?securityDecls
        ) =
        ILTypeDef(
            name = defaultArg name x.Name,
            attributes = defaultArg attributes x.Attributes,
            layout = defaultArg layout x.Layout,
            genericParams = defaultArg genericParams x.GenericParams,
            nestedTypes = defaultArg nestedTypes x.NestedTypes,
            implements = defaultArg implements x.Implements,
            extends = defaultArg extends x.Extends,
            methods = defaultArg methods x.Methods,
            securityDecls = defaultArg securityDecls x.SecurityDecls,
            fields = defaultArg fields x.Fields,
            methodImpls = defaultArg methodImpls x.MethodImpls,
            events = defaultArg events x.Events,
            properties = defaultArg properties x.Properties,
            additionalFlags = defaultArg newAdditionalFlags additionalFlags,
            customAttrs = defaultArg customAttrs (x.CustomAttrsStored)
        )

    member x.CustomAttrs: ILAttributes =
        match customAttrsStored with
        | ILAttributesStored.Reader f ->
            let res = ILAttributes(f x.MetadataIndex)
            customAttrsStored <- ILAttributesStored.Given res
            res
        | ILAttributesStored.Given res -> res

    member x.SecurityDecls = x.SecurityDeclsStored.GetSecurityDecls x.MetadataIndex

    member x.IsClass = hasFlag ILTypeDefAdditionalFlags.Class

    member x.IsStruct = hasFlag ILTypeDefAdditionalFlags.ValueType

    member x.IsInterface = hasFlag ILTypeDefAdditionalFlags.Interface

    member x.IsEnum = hasFlag ILTypeDefAdditionalFlags.Enum

    member x.IsDelegate = hasFlag ILTypeDefAdditionalFlags.Delegate

    member x.Access = typeAccessOfFlags (int x.Attributes)
    member x.IsAbstract = x.Attributes &&& TypeAttributes.Abstract <> enum 0
    member x.IsSealed = x.Attributes &&& TypeAttributes.Sealed <> enum 0
    member x.IsSerializable = x.Attributes &&& TypeAttributes.Serializable <> enum 0

    member x.IsComInterop =
        x.Attributes &&& TypeAttributes.Import
        <> enum 0 (* Class or interface generated for COM interop *)

    member x.IsSpecialName = x.Attributes &&& TypeAttributes.SpecialName <> enum 0
    member x.HasSecurity = x.Attributes &&& TypeAttributes.HasSecurity <> enum 0
    member x.Encoding = typeEncodingOfFlags (int x.Attributes)
    member x.IsStructOrEnum = x.IsStruct || x.IsEnum

    member x.WithAccess(access) =
        x.With(
            attributes =
                (x.Attributes &&& ~~~TypeAttributes.VisibilityMask
                 ||| convertTypeAccessFlags access)
        )

    member x.WithNestedAccess(access) =
        x.With(
            attributes =
                (x.Attributes &&& ~~~TypeAttributes.VisibilityMask
                 ||| convertToNestedTypeAccess access)
        )

    member x.WithSealed(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition TypeAttributes.Sealed))

    member x.WithSerializable(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition TypeAttributes.Serializable))

    member x.WithAbstract(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition TypeAttributes.Abstract))

    member x.WithImport(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition TypeAttributes.Import))

    member x.WithHasSecurity(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition TypeAttributes.HasSecurity))

    member x.WithLayout(layout) =
        x.With(attributes = (x.Attributes ||| convertLayout layout), layout = layout)

    member x.WithKind(kind) =
        x.With(
            attributes = (x.Attributes ||| convertTypeKind kind),
            newAdditionalFlags = (resetTypeKind additionalFlags ||| kind),
            extends =
                match kind with
                | HasFlag ILTypeDefAdditionalFlags.Interface -> emptyILExtends
                | _ -> x.Extends
        )

    member x.WithEncoding(encoding) =
        x.With(attributes = (x.Attributes &&& ~~~TypeAttributes.StringFormatMask ||| convertEncoding encoding))

    member x.WithSpecialName(condition) =
        x.With(attributes = (x.Attributes |> conditionalAdd condition TypeAttributes.SpecialName))

    member x.WithInitSemantics(init) =
        x.With(attributes = (x.Attributes ||| convertInitSemantics init))

    member x.WithIsKnownToBeAttribute() =
        x.With(newAdditionalFlags = (additionalFlags ||| ILTypeDefAdditionalFlags.IsKnownToBeAttribute))

    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = "type " + x.Name

and [<Sealed>] ILTypeDefs(f: unit -> ILPreTypeDef[]) =
    inherit DelayInitArrayMap<ILPreTypeDef, string list * string, ILPreTypeDef>(f)

    override this.CreateDictionary(arr) =
        let t = Dictionary(arr.Length, HashIdentity.Structural)

        for pre in arr do
            let key = pre.Namespace, pre.Name
            t[key] <- pre

        ReadOnlyDictionary t

    member x.AsArray() =
        [| for pre in x.GetArray() -> pre.GetTypeDef() |]

    member x.AsList() =
        [ for pre in x.GetArray() -> pre.GetTypeDef() ]

    interface IEnumerable with
        member x.GetEnumerator() =
            ((x :> IEnumerable<ILTypeDef>).GetEnumerator() :> IEnumerator)

    interface IEnumerable<ILTypeDef> with
        member x.GetEnumerator() =
            (seq { for pre in x.GetArray() -> pre.GetTypeDef() }).GetEnumerator()

    member x.AsArrayOfPreTypeDefs() = x.GetArray()

    member x.FindByName nm =
        let ns, n = splitILTypeName nm
        x.GetDictionary().[(ns, n)].GetTypeDef()

    member x.ExistsByName nm =
        let ns, n = splitILTypeName nm
        x.GetDictionary().ContainsKey((ns, n))

and [<NoEquality; NoComparison>] ILPreTypeDef =
    abstract Namespace: string list
    abstract Name: string
    abstract GetTypeDef: unit -> ILTypeDef

/// This is a memory-critical class. Very many of these objects get allocated and held to represent the contents of .NET assemblies.
and [<Sealed>] ILPreTypeDefImpl(nameSpace: string list, name: string, metadataIndex: int32, storage: ILTypeDefStored) =
    let stored =
        lazy
            match storage with
            | ILTypeDefStored.Given td -> td
            | ILTypeDefStored.Computed f -> f ()
            | ILTypeDefStored.Reader f -> f metadataIndex

    interface ILPreTypeDef with
        member _.Namespace = nameSpace
        member _.Name = name
        member x.GetTypeDef() = stored.Value

and ILTypeDefStored =
    | Given of ILTypeDef
    | Reader of (int32 -> ILTypeDef)
    | Computed of (unit -> ILTypeDef)

let mkILTypeDefReader f = ILTypeDefStored.Reader f

type ILNestedExportedType =
    {
        Name: string
        Access: ILMemberAccess
        Nested: ILNestedExportedTypes
        CustomAttrsStored: ILAttributesStored
        MetadataIndex: int32
    }

    member x.CustomAttrs = x.CustomAttrsStored.GetCustomAttrs x.MetadataIndex

    override x.ToString() = "exported type " + x.Name

and ILNestedExportedTypes =
    | ILNestedExportedTypes of InterruptibleLazy<Map<string, ILNestedExportedType>>

    member x.AsList() =
        let (ILNestedExportedTypes ltab) = x in Map.foldBack (fun _x y r -> y :: r) (ltab.Force()) []

and [<NoComparison; NoEquality>] ILExportedTypeOrForwarder =
    {
        ScopeRef: ILScopeRef
        Name: string
        Attributes: TypeAttributes
        Nested: ILNestedExportedTypes
        CustomAttrsStored: ILAttributesStored
        MetadataIndex: int32
    }

    member x.Access = typeAccessOfFlags (int x.Attributes)

    member x.IsForwarder = x.Attributes &&& enum<TypeAttributes> (0x00200000) <> enum 0

    member x.CustomAttrs = x.CustomAttrsStored.GetCustomAttrs x.MetadataIndex

    override x.ToString() = "exported type " + x.Name

and ILExportedTypesAndForwarders =
    | ILExportedTypesAndForwarders of InterruptibleLazy<Map<string, ILExportedTypeOrForwarder>>

    member x.AsList() =
        let (ILExportedTypesAndForwarders ltab) = x in Map.foldBack (fun _x y r -> y :: r) (ltab.Force()) []

    member x.TryFindByName nm =
        match x with
        | ILExportedTypesAndForwarders ltab -> ltab.Value.TryFind nm

[<RequireQualifiedAccess>]
type ILResourceAccess =
    | Public
    | Private

[<RequireQualifiedAccess; NoEquality; NoComparison>]
type ILResourceLocation =
    | Local of ByteStorage
    | File of ILModuleRef * int32
    | Assembly of ILAssemblyRef

type ILResource =
    {
        Name: string
        Location: ILResourceLocation
        Access: ILResourceAccess
        CustomAttrsStored: ILAttributesStored
        MetadataIndex: int32
    }

    /// Read the bytes from a resource local to an assembly
    member r.GetBytes() =
        match r.Location with
        | ILResourceLocation.Local bytes -> bytes.GetByteMemory()
        | _ -> failwith "GetBytes"

    member x.CustomAttrs = x.CustomAttrsStored.GetCustomAttrs x.MetadataIndex

    override x.ToString() = "resource " + x.Name

type ILResources =
    | ILResources of ILResource list

    member x.AsList() = let (ILResources ltab) = x in ltab

// --------------------------------------------------------------------
// One module in the "current" assembly
// --------------------------------------------------------------------

[<RequireQualifiedAccess>]
type ILAssemblyLongevity =
    | Unspecified
    | Library
    | PlatformAppDomain
    | PlatformProcess
    | PlatformSystem

    static member Default = Unspecified

type ILAssemblyManifest =
    {
        Name: string
        AuxModuleHashAlgorithm: int32
        SecurityDeclsStored: ILSecurityDeclsStored
        PublicKey: byte[] option
        Version: ILVersionInfo option
        Locale: Locale option
        CustomAttrsStored: ILAttributesStored

        AssemblyLongevity: ILAssemblyLongevity
        DisableJitOptimizations: bool
        JitTracking: bool
        IgnoreSymbolStoreSequencePoints: bool
        Retargetable: bool

        /// Records the types implemented by other modules.
        ExportedTypes: ILExportedTypesAndForwarders

        /// Records whether the entrypoint resides in another module.
        EntrypointElsewhere: ILModuleRef option
        MetadataIndex: int32
    }

    member x.CustomAttrs = x.CustomAttrsStored.GetCustomAttrs x.MetadataIndex

    member x.SecurityDecls = x.SecurityDeclsStored.GetSecurityDecls x.MetadataIndex

    override x.ToString() = "assembly manifest " + x.Name

[<RequireQualifiedAccess>]
type ILNativeResource =
    | In of fileName: string * linkedResourceBase: int * linkedResourceStart: int * linkedResourceLength: int
    | Out of unlinkedResource: byte[]

type ILModuleDef =
    {
        Manifest: ILAssemblyManifest option
        Name: string
        TypeDefs: ILTypeDefs
        SubsystemVersion: int * int
        UseHighEntropyVA: bool
        SubSystemFlags: int32
        IsDLL: bool
        IsILOnly: bool
        Platform: ILPlatform option
        StackReserveSize: int32 option
        Is32Bit: bool
        Is32BitPreferred: bool
        Is64Bit: bool
        VirtualAlignment: int32
        PhysicalAlignment: int32
        ImageBase: int32
        MetadataVersion: string
        Resources: ILResources
        /// e.g. win32 resources
        NativeResources: ILNativeResource list
        CustomAttrsStored: ILAttributesStored
        MetadataIndex: int32
    }

    member x.ManifestOfAssembly =
        match x.Manifest with
        | Some m -> m
        | None -> failwith "no manifest"

    member m.HasManifest =
        match m.Manifest with
        | None -> false
        | _ -> true

    member x.CustomAttrs = x.CustomAttrsStored.GetCustomAttrs x.MetadataIndex

    override x.ToString() = "assembly " + x.Name

// --------------------------------------------------------------------
// Add fields and types to tables, with decent error messages
// when clashes occur...
// --------------------------------------------------------------------

let mkILEmptyGenericParams = ([]: ILGenericParameterDefs)

let emptyILGenericArgsList = ([]: ILType list)

// --------------------------------------------------------------------
// Make ILTypeRefs etc.
// --------------------------------------------------------------------

let mkILNestedTyRef (scope, l, nm) = ILTypeRef.Create(scope, l, nm)

let mkILTyRef (scope, nm) = mkILNestedTyRef (scope, [], nm)

type ILGenericArgsList = ILType list

let mkILTySpec (tref, inst) = ILTypeSpec.Create(tref, inst)

let mkILNonGenericTySpec tref = mkILTySpec (tref, [])

let mkILTyRefInTyRef (tref: ILTypeRef, nm) =
    mkILNestedTyRef (tref.Scope, tref.Enclosing @ [ tref.Name ], nm)

let mkILTy boxed tspec =
    match boxed with
    | AsObject -> mkILBoxedType tspec
    | _ -> ILType.Value tspec

let mkILNamedTy vc tref tinst =
    mkILTy vc (ILTypeSpec.Create(tref, tinst))

let mkILValueTy tref tinst = mkILNamedTy AsValue tref tinst

let mkILBoxedTy tref tinst = mkILNamedTy AsObject tref tinst

let mkILNonGenericValueTy tref = mkILNamedTy AsValue tref []

let mkILNonGenericBoxedTy tref = mkILNamedTy AsObject tref []

let mkSimpleAssemblyRef n =
    ILAssemblyRef.Create(n, None, None, false, None, None)

let mkSimpleModRef n = ILModuleRef.Create(n, true, None)

// --------------------------------------------------------------------
// The toplevel class of a module is called "<Module>"
// --------------------------------------------------------------------

let typeNameForGlobalFunctions = "<Module>"

let mkILTypeForGlobalFunctions scoref =
    mkILBoxedType (mkILNonGenericTySpec (ILTypeRef.Create(scoref, [], typeNameForGlobalFunctions)))

let isTypeNameForGlobalFunctions d = (d = typeNameForGlobalFunctions)

let mkILMethRef (tref, callconv, nm, numGenericParams, argTys, retTy) =
    {
        mrefParent = tref
        mrefCallconv = callconv
        mrefGenericArity = numGenericParams
        mrefName = nm
        mrefArgs = argTys
        mrefReturn = retTy
    }

let mkILMethSpecForMethRefInTy (mref, ty, methInst) =
    {
        mspecMethodRef = mref
        mspecDeclaringType = ty
        mspecMethodInst = methInst
    }

let mkILMethSpec (mref, vc, tinst, methInst) =
    mkILMethSpecForMethRefInTy (mref, mkILNamedTy vc mref.DeclaringTypeRef tinst, methInst)

let mkILMethSpecInTypeRef (tref, vc, cc, nm, argTys, retTy, tinst, methInst) =
    mkILMethSpec (mkILMethRef (tref, cc, nm, List.length methInst, argTys, retTy), vc, tinst, methInst)

let mkILMethSpecInTy (ty: ILType, cc, nm, argTys, retTy, methInst: ILGenericArgs) =
    mkILMethSpecForMethRefInTy (mkILMethRef (ty.TypeRef, cc, nm, methInst.Length, argTys, retTy), ty, methInst)

let mkILNonGenericMethSpecInTy (ty, cc, nm, argTys, retTy) =
    mkILMethSpecInTy (ty, cc, nm, argTys, retTy, [])

let mkILInstanceMethSpecInTy (ty: ILType, nm, argTys, retTy, methInst) =
    mkILMethSpecInTy (ty, ILCallingConv.Instance, nm, argTys, retTy, methInst)

let mkILNonGenericInstanceMethSpecInTy (ty: ILType, nm, argTys, retTy) =
    mkILInstanceMethSpecInTy (ty, nm, argTys, retTy, [])

let mkILStaticMethSpecInTy (ty, nm, argTys, retTy, methInst) =
    mkILMethSpecInTy (ty, ILCallingConv.Static, nm, argTys, retTy, methInst)

let mkILNonGenericStaticMethSpecInTy (ty, nm, argTys, retTy) =
    mkILStaticMethSpecInTy (ty, nm, argTys, retTy, [])

let mkILCtorMethSpec (tref, argTys, tinst) =
    mkILMethSpecInTypeRef (tref, AsObject, ILCallingConv.Instance, ".ctor", argTys, ILType.Void, tinst, [])

let mkILCtorMethSpecForTy (ty, args) =
    mkILMethSpecInTy (ty, ILCallingConv.Instance, ".ctor", args, ILType.Void, [])

let mkILNonGenericCtorMethSpec (tref, args) = mkILCtorMethSpec (tref, args, [])

// --------------------------------------------------------------------
// Make references to fields
// --------------------------------------------------------------------

let mkILFieldRef (tref, nm, ty) =
    {
        DeclaringTypeRef = tref
        Name = nm
        Type = ty
    }

let mkILFieldSpec (tref, ty) = { FieldRef = tref; DeclaringType = ty }

let mkILFieldSpecInTy (ty: ILType, nm, fty) =
    mkILFieldSpec (mkILFieldRef (ty.TypeRef, nm, fty), ty)

let andTailness x y =
    match x with
    | Tailcall when y -> Tailcall
    | _ -> Normalcall

// --------------------------------------------------------------------
// Basic operations on code.
// --------------------------------------------------------------------

let formatCodeLabel (x: int) = "L" + string x

//  ++GLOBAL MUTABLE STATE (concurrency safe)
let codeLabelCount = ref 0
let generateCodeLabel () = Interlocked.Increment codeLabelCount

let instrIsRet i =
    match i with
    | I_ret -> true
    | _ -> false

let nonBranchingInstrsToCode instrs : ILCode =
    let instrs = Array.ofList instrs

    let instrs =
        if instrs.Length <> 0 && instrIsRet (Array.last instrs) then
            instrs
        else
            Array.append instrs [| I_ret |]

    {
        Labels = Dictionary()
        Instrs = instrs
        Exceptions = []
        Locals = []
    }

// --------------------------------------------------------------------
//
// --------------------------------------------------------------------

let mkILTyvarTy tv = ILType.TypeVar tv

let mkILSimpleTypar nm =
    {
        Name = nm
        Constraints = []
        Variance = NonVariant
        HasReferenceTypeConstraint = false
        HasNotNullableValueTypeConstraint = false
        HasDefaultConstructorConstraint = false
        HasAllowsRefStruct = false
        CustomAttrsStored = storeILCustomAttrs emptyILCustomAttrs
        MetadataIndex = NoMetadataIdx
    }

let genericParamOfGenericActual (_ga: ILType) = mkILSimpleTypar "T"

let mkILFormalTypars (x: ILGenericArgsList) = List.map genericParamOfGenericActual x

let mkILFormalGenericArgs numtypars (gparams: ILGenericParameterDefs) =
    List.mapi (fun n _gf -> mkILTyvarTy (uint16 (numtypars + n))) gparams

let mkILFormalBoxedTy tref gparams =
    mkILBoxedTy tref (mkILFormalGenericArgs 0 gparams)

let mkILFormalNamedTy bx tref gparams =
    mkILNamedTy bx tref (mkILFormalGenericArgs 0 gparams)

// --------------------------------------------------------------------
// Operations on class etc. defs.
// --------------------------------------------------------------------

let mkRefForNestedILTypeDef scope (enc: ILTypeDef list, td: ILTypeDef) =
    mkILNestedTyRef (scope, (enc |> List.map (fun etd -> etd.Name)), td.Name)

// --------------------------------------------------------------------
// Operations on type tables.
// --------------------------------------------------------------------

let mkILPreTypeDef (td: ILTypeDef) =
    let ns, n = splitILTypeName td.Name
    ILPreTypeDefImpl(ns, n, NoMetadataIdx, ILTypeDefStored.Given td) :> ILPreTypeDef

let mkILPreTypeDefComputed (ns, n, f) =
    ILPreTypeDefImpl(ns, n, NoMetadataIdx, ILTypeDefStored.Computed f) :> ILPreTypeDef

let mkILPreTypeDefRead (ns, n, idx, f) =
    ILPreTypeDefImpl(ns, n, idx, f) :> ILPreTypeDef

let addILTypeDef td (tdefs: ILTypeDefs) =
    ILTypeDefs(fun () -> [| yield mkILPreTypeDef td; yield! tdefs.AsArrayOfPreTypeDefs() |])

let mkILTypeDefsFromArray (l: ILTypeDef[]) =
    ILTypeDefs(fun () -> Array.map mkILPreTypeDef l)

let mkILTypeDefs l = mkILTypeDefsFromArray (Array.ofList l)
let mkILTypeDefsComputed f = ILTypeDefs f
let emptyILTypeDefs = mkILTypeDefsFromArray [||]

let emptyILInterfaceImpls = InterruptibleLazy<InterfaceImpl list>.FromValue([])

// --------------------------------------------------------------------
// Operations on method tables.
// --------------------------------------------------------------------

let mkILMethodsFromArray xs = ILMethodDefs(fun () -> xs)

let mkILMethods xs =
    xs |> Array.ofList |> mkILMethodsFromArray

let mkILMethodsComputed f = ILMethodDefs f
let emptyILMethods = mkILMethodsFromArray [||]

// --------------------------------------------------------------------
// Operations and defaults for modules, assemblies etc.
// --------------------------------------------------------------------

let defaultSubSystem = 3 (* this is what comes out of ILDASM on 30/04/2001 *)
let defaultPhysAlignment = 512 (* this is what comes out of ILDASM on 30/04/2001 *)

let defaultVirtAlignment =
    0x2000 (* this is what comes out of ILDASM on 30/04/2001 *)

let defaultImageBase =
    0x034f0000 (* this is what comes out of ILDASM on 30/04/2001 *)

// --------------------------------------------------------------------
// Array types
// --------------------------------------------------------------------

let mkILArrTy (ty, shape) = ILType.Array(shape, ty)

let mkILArr1DTy ty =
    mkILArrTy (ty, ILArrayShape.SingleDimensional)

let isILArrTy ty =
    match ty with
    | ILType.Array _ -> true
    | _ -> false

let destILArrTy ty =
    match ty with
    | ILType.Array(shape, ty) -> (shape, ty)
    | _ -> failwith "destILArrTy"

// --------------------------------------------------------------------
// Sigs of special types built-in
// --------------------------------------------------------------------

[<Literal>]
let tname_Attribute = "System.Attribute"

[<Literal>]
let tname_Enum = "System.Enum"

[<Literal>]
let tname_SealedAttribute = "System.SealedAttribute"

[<Literal>]
let tname_Object = "System.Object"

[<Literal>]
let tname_String = "System.String"

[<Literal>]
let tname_Array = "System.Array"

[<Literal>]
let tname_Type = "System.Type"

[<Literal>]
let tname_Int64 = "System.Int64"

[<Literal>]
let tname_UInt64 = "System.UInt64"

[<Literal>]
let tname_Int32 = "System.Int32"

[<Literal>]
let tname_UInt32 = "System.UInt32"

[<Literal>]
let tname_Int16 = "System.Int16"

[<Literal>]
let tname_UInt16 = "System.UInt16"

[<Literal>]
let tname_SByte = "System.SByte"

[<Literal>]
let tname_Byte = "System.Byte"

[<Literal>]
let tname_Single = "System.Single"

[<Literal>]
let tname_Double = "System.Double"

[<Literal>]
let tname_Bool = "System.Boolean"

[<Literal>]
let tname_Char = "System.Char"

[<Literal>]
let tname_IntPtr = "System.IntPtr"

[<Literal>]
let tname_UIntPtr = "System.UIntPtr"

[<Literal>]
let tname_TypedReference = "System.TypedReference"

[<NoEquality; NoComparison; StructuredFormatDisplay("{DebugText}")>]
type ILGlobals(primaryScopeRef: ILScopeRef, equivPrimaryAssemblyRefs: ILAssemblyRef list, fsharpCoreAssemblyScopeRef: ILScopeRef) =

    let equivPrimaryAssemblyRefs = Array.ofList equivPrimaryAssemblyRefs

    let mkSysILTypeRef nm = mkILTyRef (primaryScopeRef, nm)

    let byteIlType = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_Byte))

    let stringIlType =
        mkILBoxedType (mkILNonGenericTySpec (mkSysILTypeRef tname_String))

    member _.primaryAssemblyScopeRef = primaryScopeRef

    member x.primaryAssemblyRef =
        match primaryScopeRef with
        | ILScopeRef.Assembly aref -> aref
        | _ -> failwith "Invalid primary assembly"

    member x.primaryAssemblyName = x.primaryAssemblyRef.Name

    member val typ_Attribute = mkILBoxedType (mkILNonGenericTySpec (mkSysILTypeRef tname_Attribute))

    member val typ_Enum = mkILBoxedType (mkILNonGenericTySpec (mkSysILTypeRef tname_Enum))

    member val typ_SealedAttribute = mkILBoxedType (mkILNonGenericTySpec (mkSysILTypeRef tname_SealedAttribute))

    member val typ_Object = mkILBoxedType (mkILNonGenericTySpec (mkSysILTypeRef tname_Object))

    member val typ_String = stringIlType

    member val typ_Array = mkILBoxedType (mkILNonGenericTySpec (mkSysILTypeRef tname_Array))

    member val typ_Type = mkILBoxedType (mkILNonGenericTySpec (mkSysILTypeRef tname_Type))

    member val typ_SByte = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_SByte))

    member val typ_Int16 = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_Int16))

    member val typ_Int32 = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_Int32))

    member val typ_Int64 = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_Int64))

    member val typ_Byte = byteIlType

    member val typ_ByteArray = ILType.Array(ILArrayShape.SingleDimensional, byteIlType)

    member val typ_StringArray = ILType.Array(ILArrayShape.SingleDimensional, stringIlType)

    member val typ_UInt16 = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_UInt16))

    member val typ_UInt32 = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_UInt32))

    member val typ_UInt64 = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_UInt64))

    member val typ_Single = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_Single))

    member val typ_Double = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_Double))

    member val typ_Bool = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_Bool))

    member val typ_Char = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_Char))

    member val typ_IntPtr = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_IntPtr))

    member val typ_UIntPtr = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_UIntPtr))

    member val typ_TypedReference = ILType.Value(mkILNonGenericTySpec (mkSysILTypeRef tname_TypedReference))

    member _.fsharpCoreAssemblyScopeRef = fsharpCoreAssemblyScopeRef

    member x.IsPossiblePrimaryAssemblyRef(aref: ILAssemblyRef) =
        aref.EqualsIgnoringVersion x.primaryAssemblyRef
        || equivPrimaryAssemblyRefs |> Array.exists aref.EqualsIgnoringVersion

    /// For debugging
    [<DebuggerBrowsable(DebuggerBrowsableState.Never)>]
    member x.DebugText = x.ToString()

    override x.ToString() = "<ILGlobals>"

let mkILGlobals (primaryScopeRef, equivPrimaryAssemblyRefs, fsharpCoreAssemblyScopeRef) =
    ILGlobals(primaryScopeRef, equivPrimaryAssemblyRefs, fsharpCoreAssemblyScopeRef)

let mkNormalCall mspec = I_call(Normalcall, mspec, None)

let mkNormalCallvirt mspec = I_callvirt(Normalcall, mspec, None)

let mkNormalNewobj mspec = I_newobj(mspec, None)

/// Comment on common object cache sizes:
/// mkLdArg - I can't imagine any IL method we generate needing more than this
/// mkLdLoc - I tried 256, and there were LdLoc allocations left, so I upped it o 512. I didn't check again.
/// mkStLoc - it should be the same as LdLoc (where there's a LdLoc there must be a StLoc)
/// mkLdcInt32 - just a guess

let ldargs = [| for i in 0..128 -> I_ldarg(uint16 i) |]

let mkLdarg i =
    if 0us < i && i < uint16 ldargs.Length then
        ldargs[int i]
    else
        I_ldarg i

let mkLdarg0 = mkLdarg 0us

let ldlocs = [| for i in 0..512 -> I_ldloc(uint16 i) |]

let mkLdloc i =
    if 0us < i && i < uint16 ldlocs.Length then
        ldlocs[int i]
    else
        I_ldloc i

let stlocs = [| for i in 0..512 -> I_stloc(uint16 i) |]

let mkStloc i =
    if 0us < i && i < uint16 stlocs.Length then
        stlocs[int i]
    else
        I_stloc i

let ldi32s = [| for i in 0..256 -> AI_ldc(DT_I4, ILConst.I4 i) |]

let mkLdcInt32 i =
    if 0 < i && i < ldi32s.Length then
        ldi32s[i]
    else
        AI_ldc(DT_I4, ILConst.I4 i)

(* NOTE: ecma_ prefix refers to the standard "mscorlib" *)
let ecmaPublicKey =
    PublicKeyToken(Bytes.ofInt32Array [| 0xde; 0xad; 0xbe; 0xef; 0xca; 0xfe; 0xfa; 0xce |])

let isILBoxedTy =
    function
    | ILType.Boxed _ -> true
    | _ -> false

let isILValueTy =
    function
    | ILType.Value _ -> true
    | _ -> false

let rec stripILModifiedFromTy (ty: ILType) =
    match ty with
    | ILType.Modified(_, _, ty) -> stripILModifiedFromTy ty
    | _ -> ty

let isBuiltInTySpec (ilg: ILGlobals) (tspec: ILTypeSpec) n =
    let tref = tspec.TypeRef
    let scoref = tref.Scope

    tref.Name = n
    && (match scoref with
        | ILScopeRef.Local
        | ILScopeRef.Module _ -> false
        | ILScopeRef.Assembly aref -> ilg.IsPossiblePrimaryAssemblyRef aref
        | ILScopeRef.PrimaryAssembly -> true)

let isILBoxedBuiltInTy ilg (ty: ILType) n =
    isILBoxedTy ty && isBuiltInTySpec ilg ty.TypeSpec n

let isILValueBuiltInTy ilg (ty: ILType) n =
    isILValueTy ty && isBuiltInTySpec ilg ty.TypeSpec n

let isILObjectTy ilg ty = isILBoxedBuiltInTy ilg ty tname_Object

let isILStringTy ilg ty = isILBoxedBuiltInTy ilg ty tname_String

let isILTypedReferenceTy ilg ty =
    isILValueBuiltInTy ilg ty tname_TypedReference

let isILSByteTy ilg ty = isILValueBuiltInTy ilg ty tname_SByte

let isILByteTy ilg ty = isILValueBuiltInTy ilg ty tname_Byte

let isILInt16Ty ilg ty = isILValueBuiltInTy ilg ty tname_Int16

let isILUInt16Ty ilg ty = isILValueBuiltInTy ilg ty tname_UInt16

let isILInt32Ty ilg ty = isILValueBuiltInTy ilg ty tname_Int32

let isILUInt32Ty ilg ty = isILValueBuiltInTy ilg ty tname_UInt32

let isILInt64Ty ilg ty = isILValueBuiltInTy ilg ty tname_Int64

let isILUInt64Ty ilg ty = isILValueBuiltInTy ilg ty tname_UInt64

let isILIntPtrTy ilg ty = isILValueBuiltInTy ilg ty tname_IntPtr

let isILUIntPtrTy ilg ty = isILValueBuiltInTy ilg ty tname_UIntPtr

let isILBoolTy ilg ty = isILValueBuiltInTy ilg ty tname_Bool

let isILCharTy ilg ty = isILValueBuiltInTy ilg ty tname_Char

let isILSingleTy ilg ty = isILValueBuiltInTy ilg ty tname_Single

let isILDoubleTy ilg ty = isILValueBuiltInTy ilg ty tname_Double

// --------------------------------------------------------------------
// Rescoping
// --------------------------------------------------------------------

let rescopeILScopeRef scoref scoref1 =
    match scoref, scoref1 with
    | _, ILScopeRef.Local -> scoref
    | ILScopeRef.Local, _ -> scoref1
    | _, ILScopeRef.Module _ -> scoref
    | ILScopeRef.Module _, _ -> scoref1
    | _ -> scoref1

let rescopeILTypeRef scoref (tref1: ILTypeRef) =
    let scoref1 = tref1.Scope
    let scoref2 = rescopeILScopeRef scoref scoref1

    if scoref1 === scoref2 then
        tref1
    else
        ILTypeRef.Create(scoref2, tref1.Enclosing, tref1.Name)

// ORIGINAL IMPLEMENTATION (too many allocations
//         { tspecTypeRef=rescopeILTypeRef scoref tref
//           tspecInst=rescopeILTypes scoref tinst }
let rec rescopeILTypeSpec scoref (tspec1: ILTypeSpec) =
    let tref1 = tspec1.TypeRef
    let tinst1 = tspec1.GenericArgs
    let tref2 = rescopeILTypeRef scoref tref1

    // avoid reallocation in the common case
    if tref1 === tref2 then
        if isNil tinst1 then
            tspec1
        else
            let tinst2 = rescopeILTypes scoref tinst1

            if tinst1 === tinst2 then
                tspec1
            else
                ILTypeSpec.Create(tref2, tinst2)
    else
        let tinst2 = rescopeILTypes scoref tinst1
        ILTypeSpec.Create(tref2, tinst2)

and rescopeILType scoref ty =
    match ty with
    | ILType.Ptr t -> ILType.Ptr(rescopeILType scoref t)
    | ILType.FunctionPointer t -> ILType.FunctionPointer(rescopeILCallSig scoref t)
    | ILType.Byref t -> ILType.Byref(rescopeILType scoref t)
    | ILType.Boxed cr1 ->
        let cr2 = rescopeILTypeSpec scoref cr1

        if cr1 === cr2 then ty else mkILBoxedType cr2
    | ILType.Array(s, ety1) ->
        let ety2 = rescopeILType scoref ety1

        if ety1 === ety2 then ty else ILType.Array(s, ety2)
    | ILType.Value cr1 ->
        let cr2 = rescopeILTypeSpec scoref cr1

        if cr1 === cr2 then ty else ILType.Value cr2
    | ILType.Modified(b, tref, ty) -> ILType.Modified(b, rescopeILTypeRef scoref tref, rescopeILType scoref ty)
    | x -> x

and rescopeILTypes scoref i =
    if isNil i then i else List.mapq (rescopeILType scoref) i

and rescopeILCallSig scoref csig =
    mkILCallSig (csig.CallingConv, rescopeILTypes scoref csig.ArgTypes, rescopeILType scoref csig.ReturnType)

let rescopeILMethodRef scoref (x: ILMethodRef) =
    {
        mrefParent = rescopeILTypeRef scoref x.DeclaringTypeRef
        mrefCallconv = x.mrefCallconv
        mrefGenericArity = x.mrefGenericArity
        mrefName = x.mrefName
        mrefArgs = rescopeILTypes scoref x.mrefArgs
        mrefReturn = rescopeILType scoref x.mrefReturn
    }

let rescopeILFieldRef scoref x =
    {
        DeclaringTypeRef = rescopeILTypeRef scoref x.DeclaringTypeRef
        Name = x.Name
        Type = rescopeILType scoref x.Type
    }

// --------------------------------------------------------------------
// Instantiate polymorphism in types
// --------------------------------------------------------------------

let rec instILTypeSpecAux numFree inst (tspec: ILTypeSpec) =
    ILTypeSpec.Create(tspec.TypeRef, instILGenericArgsAux numFree inst tspec.GenericArgs)

and instILTypeAux numFree (inst: ILGenericArgs) ty =
    match ty with
    | ILType.Ptr t -> ILType.Ptr(instILTypeAux numFree inst t)
    | ILType.FunctionPointer t -> ILType.FunctionPointer(instILCallSigAux numFree inst t)
    | ILType.Array(a, t) -> ILType.Array(a, instILTypeAux numFree inst t)
    | ILType.Byref t -> ILType.Byref(instILTypeAux numFree inst t)
    | ILType.Boxed cr -> mkILBoxedType (instILTypeSpecAux numFree inst cr)
    | ILType.Value cr -> ILType.Value(instILTypeSpecAux numFree inst cr)
    | ILType.TypeVar v ->
        let v = int v
        let top = inst.Length

        if v < numFree then
            ty
        else if v - numFree >= top then
            ILType.TypeVar(uint16 (v - top))
        else
            List.item (v - numFree) inst
    | x -> x

and instILGenericArgsAux numFree inst i = List.map (instILTypeAux numFree inst) i

and instILCallSigAux numFree inst csig =
    mkILCallSig (csig.CallingConv, List.map (instILTypeAux numFree inst) csig.ArgTypes, instILTypeAux numFree inst csig.ReturnType)

let instILType i t = instILTypeAux 0 i t

// --------------------------------------------------------------------
// MS-IL: Parameters, Return types and Locals
// --------------------------------------------------------------------

let mkILParam (name, ty) : ILParameter =
    {
        Name = name
        Default = None
        Marshal = None
        IsIn = false
        IsOut = false
        IsOptional = false
        Type = ty
        CustomAttrsStored = storeILCustomAttrs emptyILCustomAttrs
        MetadataIndex = NoMetadataIdx
    }

let mkILParamNamed (s, ty) = mkILParam (Some s, ty)

let mkILParamAnon ty = mkILParam (None, ty)

let mkILReturn ty : ILReturn =
    {
        Marshal = None
        Type = ty
        CustomAttrsStored = storeILCustomAttrs emptyILCustomAttrs
        MetadataIndex = NoMetadataIdx
    }

let mkILLocal ty dbgInfo : ILLocal =
    {
        IsPinned = false
        Type = ty
        DebugInfo = dbgInfo
    }

type ILFieldSpec with

    member fr.ActualType =
        let env = fr.DeclaringType.GenericArgs
        instILType env fr.FormalType

// --------------------------------------------------------------------
// Make a method mbody
// --------------------------------------------------------------------

let mkILMethodBody (initlocals, locals, maxstack, code, tag, imports) : ILMethodBody =
    {
        IsZeroInit = initlocals
        MaxStack = maxstack
        NoInlining = false
        AggressiveInlining = false
        Locals = locals
        Code = code
        DebugRange = tag
        DebugImports = imports
    }

let mkMethodBody (zeroinit, locals, maxstack, code, tag, imports) =
    let ilCode = mkILMethodBody (zeroinit, locals, maxstack, code, tag, imports)
    MethodBody.IL(InterruptibleLazy.FromValue ilCode)

// --------------------------------------------------------------------
// Make a constructor
// --------------------------------------------------------------------

let mkILVoidReturn = mkILReturn ILType.Void

let methBodyNotAvailable = notlazy MethodBody.NotAvailable

let methBodyAbstract = notlazy MethodBody.Abstract

let methBodyNative = notlazy MethodBody.Native

let mkILCtor (access, args, impl) =
    ILMethodDef(
        name = ".ctor",
        attributes =
            (convertMemberAccess access
             ||| MethodAttributes.SpecialName
             ||| MethodAttributes.RTSpecialName),
        implAttributes = MethodImplAttributes.Managed,
        callingConv = ILCallingConv.Instance,
        parameters = args,
        ret = mkILVoidReturn,
        body = notlazy impl,
        securityDecls = emptyILSecurityDecls,
        isEntryPoint = false,
        genericParams = mkILEmptyGenericParams,
        customAttrs = emptyILCustomAttrs
    )

// --------------------------------------------------------------------
// Do-nothing ctor, just pass on to monomorphic superclass
// --------------------------------------------------------------------

let mkCallBaseConstructor (ty, args: ILType list) =
    [ mkLdarg0 ]
    @ List.mapi (fun i _ -> mkLdarg (uint16 (i + 1))) args
    @ [ mkNormalCall (mkILCtorMethSpecForTy (ty, [])) ]

let mkNormalStfld fspec = I_stfld(Aligned, Nonvolatile, fspec)

let mkNormalStsfld fspec = I_stsfld(Nonvolatile, fspec)

let mkNormalLdsfld fspec = I_ldsfld(Nonvolatile, fspec)

let mkNormalLdfld fspec = I_ldfld(Aligned, Nonvolatile, fspec)

let mkNormalLdflda fspec = I_ldflda fspec

let mkNormalLdobj dt = I_ldobj(Aligned, Nonvolatile, dt)

let mkNormalStobj dt = I_stobj(Aligned, Nonvolatile, dt)

let mkILNonGenericEmptyCtor (superTy, tag, imports) =
    let ctor = mkCallBaseConstructor (superTy, [])
    let body = mkMethodBody (false, [], 8, nonBranchingInstrsToCode ctor, tag, imports)
    mkILCtor (ILMemberAccess.Public, [], body)

// --------------------------------------------------------------------
// Make a static, top level monomorphic method - very useful for
// creating helper ILMethodDefs for internal use.
// --------------------------------------------------------------------

let mkILStaticMethod (genparams, nm, access, args, ret, impl) =
    ILMethodDef(
        genericParams = genparams,
        name = nm,
        attributes = (convertMemberAccess access ||| MethodAttributes.Static),
        implAttributes = MethodImplAttributes.Managed,
        callingConv = ILCallingConv.Static,
        parameters = args,
        ret = ret,
        securityDecls = emptyILSecurityDecls,
        isEntryPoint = false,
        customAttrs = emptyILCustomAttrs,
        body = notlazy impl
    )

let mkILNonGenericStaticMethod (nm, access, args, ret, impl) =
    mkILStaticMethod (mkILEmptyGenericParams, nm, access, args, ret, impl)

let mkILClassCtor impl =
    ILMethodDef(
        name = ".cctor",
        attributes =
            (MethodAttributes.Private
             ||| MethodAttributes.Static
             ||| MethodAttributes.SpecialName
             ||| MethodAttributes.RTSpecialName),
        implAttributes = MethodImplAttributes.Managed,
        callingConv = ILCallingConv.Static,
        genericParams = mkILEmptyGenericParams,
        parameters = [],
        ret = mkILVoidReturn,
        isEntryPoint = false,
        securityDecls = emptyILSecurityDecls,
        customAttrs = emptyILCustomAttrs,
        body = notlazy impl
    )

let mkILGenericVirtualMethod (nm, callconv: ILCallingConv, access, genparams, actual_args, actual_ret, impl) =
    let attributes =
        convertMemberAccess access
        ||| MethodAttributes.CheckAccessOnOverride
        ||| (match impl with
             | MethodBody.Abstract -> MethodAttributes.Abstract ||| MethodAttributes.Virtual
             | _ -> MethodAttributes.Virtual)
        ||| (if callconv.IsInstance then
                 enum 0
             else
                 MethodAttributes.Static)

    ILMethodDef(
        name = nm,
        attributes = attributes,
        implAttributes = MethodImplAttributes.Managed,
        genericParams = genparams,
        callingConv = callconv,
        parameters = actual_args,
        ret = actual_ret,
        isEntryPoint = false,
        securityDecls = emptyILSecurityDecls,
        customAttrs = emptyILCustomAttrs,
        body = notlazy impl
    )

let mkILNonGenericVirtualMethod (nm, callconv, access, args, ret, impl) =
    mkILGenericVirtualMethod (nm, callconv, access, mkILEmptyGenericParams, args, ret, impl)

let mkILNonGenericVirtualInstanceMethod (nm, access, args, ret, impl) =
    mkILNonGenericVirtualMethod (nm, ILCallingConv.Instance, access, args, ret, impl)

let mkILGenericNonVirtualMethod (nm, access, genparams, actual_args, actual_ret, impl) =
    ILMethodDef(
        name = nm,
        attributes = (convertMemberAccess access ||| MethodAttributes.HideBySig),
        implAttributes = MethodImplAttributes.Managed,
        genericParams = genparams,
        callingConv = ILCallingConv.Instance,
        parameters = actual_args,
        ret = actual_ret,
        isEntryPoint = false,
        securityDecls = emptyILSecurityDecls,
        customAttrs = emptyILCustomAttrs,
        body = notlazy impl
    )

let mkILNonGenericInstanceMethod (nm, access, args, ret, impl) =
    mkILGenericNonVirtualMethod (nm, access, mkILEmptyGenericParams, args, ret, impl)

// --------------------------------------------------------------------
// Add some code to the end of the .cctor for a type. Create a .cctor
// if one doesn't exist already.
// --------------------------------------------------------------------

let ilmbody_code2code f (il: ILMethodBody) = { il with Code = f il.Code }

let mdef_code2code f (md: ILMethodDef) =
    let il =
        match md.Body with
        | MethodBody.IL il -> il
        | _ -> failwith "mdef_code2code - method not IL"

    let ilCode = ilmbody_code2code f il.Value
    let b = MethodBody.IL(notlazy ilCode)
    md.With(body = notlazy b)

let appendInstrsToCode (instrs: ILInstr list) (c2: ILCode) =
    let instrs = Array.ofList instrs

    match
        c2.Instrs
        |> Array.tryFindIndexBack (fun instr ->
            match instr with
            | I_ret -> true
            | _ -> false)
    with
    | Some 0 ->
        { c2 with
            Instrs = Array.concat [| instrs; c2.Instrs |]
        }
    | Some index ->
        { c2 with
            Instrs = Array.concat [| c2.Instrs[.. index - 1]; instrs; c2.Instrs[index..] |]
        }
    | None ->
        { c2 with
            Instrs = Array.append c2.Instrs instrs
        }

let prependInstrsToCode (instrs: ILInstr list) (c2: ILCode) =
    let instrs = Array.ofList instrs
    let n = instrs.Length

    match c2.Instrs[0] with
    // If there is a sequence point as the first instruction then keep it at the front
    | I_seqpoint _ as i0 ->
        let labels =
            let dict = Dictionary.newWithSize (c2.Labels.Count * 2) // Decrease chance of collisions by oversizing the hashtable

            for kvp in c2.Labels do
                dict.Add(kvp.Key, (if kvp.Value = 0 then 0 else kvp.Value + n))

            dict

        { c2 with
            Labels = labels
            Instrs = Array.concat [| [| i0 |]; instrs; c2.Instrs[1..] |]
        }
    | _ ->
        let labels =
            let dict = Dictionary.newWithSize (c2.Labels.Count * 2) // Decrease chance of collisions by oversizing the hashtable

            for kvp in c2.Labels do
                dict.Add(kvp.Key, kvp.Value + n)

            dict

        { c2 with
            Labels = labels
            Instrs = Array.append instrs c2.Instrs
        }

let appendInstrsToMethod newCode md =
    mdef_code2code (appendInstrsToCode newCode) md

let prependInstrsToMethod newCode md =
    mdef_code2code (prependInstrsToCode newCode) md

// Creates cctor if needed
let cdef_cctorCode2CodeOrCreate tag imports f (cd: ILTypeDef) =
    let mdefs = cd.Methods

    let cctor =
        match mdefs.FindByName ".cctor" with
        | [ mdef ] -> mdef
        | [] ->
            let body = mkMethodBody (false, [], 1, nonBranchingInstrsToCode [], tag, imports)
            mkILClassCtor body
        | _ -> failwith "bad method table: more than one .cctor found"

    let methods =
        ILMethodDefs(fun () ->
            [|
                yield f cctor
                for md in mdefs do
                    if md.Name <> ".cctor" then
                        yield md
            |])

    cd.With(methods = methods)

let mkRefToILMethod (tref, md: ILMethodDef) =
    mkILMethRef (tref, md.CallingConv, md.Name, md.GenericParams.Length, md.ParameterTypes, md.Return.Type)

let mkRefToILField (tref, fdef: ILFieldDef) =
    mkILFieldRef (tref, fdef.Name, fdef.FieldType)

let mkRefForILMethod scope (tdefs, tdef) mdef =
    mkRefToILMethod (mkRefForNestedILTypeDef scope (tdefs, tdef), mdef)

let mkRefForILField scope (tdefs, tdef) (fdef: ILFieldDef) =
    mkILFieldRef (mkRefForNestedILTypeDef scope (tdefs, tdef), fdef.Name, fdef.FieldType)

// Creates cctor if needed
let prependInstrsToClassCtor instrs tag imports cd =
    cdef_cctorCode2CodeOrCreate tag imports (prependInstrsToMethod instrs) cd

let mkILField (isStatic, nm, ty, init: ILFieldInit option, at: byte[] option, access, isLiteral) =
    ILFieldDef(
        name = nm,
        fieldType = ty,
        attributes =
            (convertFieldAccess access
             ||| (if isStatic then FieldAttributes.Static else enum 0)
             ||| (if isLiteral then FieldAttributes.Literal else enum 0)
             ||| (if init.IsSome then FieldAttributes.HasDefault else enum 0)
             ||| (if at.IsSome then FieldAttributes.HasFieldRVA else enum 0)),
        literalValue = init,
        data = at,
        offset = None,
        marshal = None,
        customAttrs = emptyILCustomAttrs
    )

let mkILInstanceField (nm, ty, init, access) =
    mkILField (false, nm, ty, init, None, access, false)

let mkILStaticField (nm, ty, init, at, access) =
    mkILField (true, nm, ty, init, at, access, false)

let mkILStaticLiteralField (nm, ty, init, at, access) =
    mkILField (true, nm, ty, Some init, at, access, true)

let mkILLiteralField (nm, ty, init, at, access) =
    mkILField (true, nm, ty, Some init, at, access, true)

// --------------------------------------------------------------------
// Scopes for allocating new temporary variables.
// --------------------------------------------------------------------

type ILLocalsAllocator(preAlloc: int) =
    let newLocals = ResizeArray<ILLocal>()

    member tmps.AllocLocal loc =
        let locn = uint16 (preAlloc + newLocals.Count)
        newLocals.Add loc
        locn

    member tmps.Close() = ResizeArray.toList newLocals

let mkILFieldsLazy l =
    ILFields(LazyOrderedMultiMap((fun (fdef: ILFieldDef) -> fdef.Name), l))

let mkILFields l = mkILFieldsLazy (notlazy l)

let emptyILFields = mkILFields []

let mkILEventsLazy l =
    ILEvents(LazyOrderedMultiMap((fun (edef: ILEventDef) -> edef.Name), l))

let mkILEvents l = mkILEventsLazy (notlazy l)

let emptyILEvents = mkILEvents []

let mkILPropertiesLazy l =
    ILProperties(LazyOrderedMultiMap((fun (pdef: ILPropertyDef) -> pdef.Name), l))

let mkILProperties l = mkILPropertiesLazy (notlazy l)

let emptyILProperties = mkILProperties []

let addExportedTypeToTable (y: ILExportedTypeOrForwarder) tab = Map.add y.Name y tab

let mkILExportedTypes l =
    ILExportedTypesAndForwarders(notlazy (List.foldBack addExportedTypeToTable l Map.empty))

let mkILExportedTypesLazy (l: Lazy<_>) =
    ILExportedTypesAndForwarders(InterruptibleLazy(fun _ -> List.foldBack addExportedTypeToTable (l.Force()) Map.empty))

let addNestedExportedTypeToTable (y: ILNestedExportedType) tab = Map.add y.Name y tab

let mkTypeForwarder scopeRef name nested customAttrs access =
    {
        ScopeRef = scopeRef
        Name = name
        Attributes = enum<TypeAttributes> (0x00200000) ||| convertTypeAccessFlags access
        Nested = nested
        CustomAttrsStored = storeILCustomAttrs customAttrs
        MetadataIndex = NoMetadataIdx
    }

let mkILNestedExportedTypes l =
    ILNestedExportedTypes(notlazy (List.foldBack addNestedExportedTypeToTable l Map.empty))

let mkILNestedExportedTypesLazy (l: Lazy<_>) =
    ILNestedExportedTypes(InterruptibleLazy(fun _ -> List.foldBack addNestedExportedTypeToTable (l.Force()) Map.empty))

let mkILResources l = ILResources l
let emptyILResources = ILResources []

let addMethodImplToTable y tab =
    let key = (y.Overrides.MethodRef.Name, y.Overrides.MethodRef.ArgTypes.Length)
    let prev = Map.tryFindMulti key tab
    Map.add key (y :: prev) tab

let mkILMethodImpls l =
    ILMethodImpls(notlazy (List.foldBack addMethodImplToTable l Map.empty))

let mkILMethodImplsLazy l =
    ILMethodImpls(InterruptibleLazy(fun _ -> List.foldBack addMethodImplToTable (Lazy.force l) Map.empty))

let emptyILMethodImpls = mkILMethodImpls []

/// Make a constructor that simply takes its arguments and stuffs
/// them in fields. preblock is how to call the superclass constructor....
let mkILStorageCtorWithParamNames (preblock: ILInstr list, ty, extraParams, flds, access, tag, imports) =
    let code =
        [
            match tag with
            | Some x -> I_seqpoint x
            | None -> ()
            yield! preblock
            for (n, (_pnm, nm, fieldTy, _attrs)) in List.indexed flds do
                mkLdarg0
                mkLdarg (uint16 (n + 1))
                mkNormalStfld (mkILFieldSpecInTy (ty, nm, fieldTy))
        ]

    let body = mkMethodBody (false, [], 2, nonBranchingInstrsToCode code, tag, imports)

    let fieldParams =
        [
            for (pnm, _, ty, attrs) in flds do
                let ilParam = mkILParamNamed (pnm, ty)

                let ilParam =
                    match attrs with
                    | [] -> ilParam
                    | attrs ->
                        { ilParam with
                            CustomAttrsStored = storeILCustomAttrs (mkILCustomAttrs attrs)
                        }

                yield ilParam
        ]

    mkILCtor (access, fieldParams @ extraParams, body)

let mkILSimpleStorageCtorWithParamNames (baseTySpec, ty, extraParams, flds, access, tag, imports) =
    let preblock =
        match baseTySpec with
        | None -> []
        | Some tspec -> [ mkLdarg0; mkNormalCall (mkILCtorMethSpecForTy (mkILBoxedType tspec, [])) ]

    mkILStorageCtorWithParamNames (preblock, ty, extraParams, flds, access, tag, imports)

let addParamNames flds =
    flds |> List.map (fun (nm, ty, attrs) -> (nm, nm, ty, attrs))

let mkILSimpleStorageCtor (baseTySpec, ty, extraParams, flds, access, tag, imports) =
    mkILSimpleStorageCtorWithParamNames (baseTySpec, ty, extraParams, addParamNames flds, access, tag, imports)

let mkILStorageCtor (preblock, ty, flds, access, tag, imports) =
    mkILStorageCtorWithParamNames (preblock, ty, [], addParamNames flds, access, tag, imports)

let mkILGenericClass (nm, access, genparams, extends, impls, methods, fields, nestedTypes, props, events, attrs, init) =
    let attributes =
        convertTypeAccessFlags access
        ||| TypeAttributes.AutoLayout
        ||| TypeAttributes.Class
        ||| (match init with
             | ILTypeInit.BeforeField -> TypeAttributes.BeforeFieldInit
             | _ -> enum 0)
        ||| TypeAttributes.AnsiClass

    ILTypeDef(
        name = nm,
        attributes = attributes,
        genericParams = genparams,
        implements = impls,
        layout = ILTypeDefLayout.Auto,
        extends = Some extends,
        methods = methods,
        fields = fields,
        nestedTypes = nestedTypes,
        customAttrs = storeILCustomAttrs attrs,
        methodImpls = emptyILMethodImpls,
        properties = props,
        events = events,
        securityDecls = emptyILSecurityDecls
    )

let mkRawDataValueTypeDef (iltyp_ValueType: ILType) (nm, size, pack) =
    ILTypeDef(
        name = nm,
        genericParams = [],
        attributes =
            (TypeAttributes.NotPublic
             ||| TypeAttributes.Sealed
             ||| TypeAttributes.ExplicitLayout
             ||| TypeAttributes.BeforeFieldInit
             ||| TypeAttributes.AnsiClass),
        implements = [],
        extends = Some iltyp_ValueType,
        layout = ILTypeDefLayout.Explicit { Size = Some size; Pack = Some pack },
        methods = emptyILMethods,
        fields = emptyILFields,
        nestedTypes = emptyILTypeDefs,
        customAttrs = emptyILCustomAttrsStored,
        methodImpls = emptyILMethodImpls,
        properties = emptyILProperties,
        events = emptyILEvents,
        securityDecls = emptyILSecurityDecls
    )

let mkILSimpleClass (ilg: ILGlobals) (nm, access, methods, fields, nestedTypes, props, events, attrs, init) =
    mkILGenericClass (nm, access, mkILEmptyGenericParams, ilg.typ_Object, [], methods, fields, nestedTypes, props, events, attrs, init)

let mkILTypeDefForGlobalFunctions ilg (methods, fields) =
    mkILSimpleClass
        ilg
        (typeNameForGlobalFunctions,
         ILTypeDefAccess.Public,
         methods,
         fields,
         emptyILTypeDefs,
         emptyILProperties,
         emptyILEvents,
         emptyILCustomAttrs,
         ILTypeInit.BeforeField)

let destTypeDefsWithGlobalFunctionsFirst ilg (tdefs: ILTypeDefs) =
    let l = tdefs.AsList()

    let top, nontop =
        l |> List.partition (fun td -> td.Name = typeNameForGlobalFunctions)

    let top2 =
        if isNil top then
            [ mkILTypeDefForGlobalFunctions ilg (emptyILMethods, emptyILFields) ]
        else
            top

    top2 @ nontop

let mkILSimpleModule
    assemblyName
    moduleName
    dll
    subsystemVersion
    useHighEntropyVA
    tdefs
    hashalg
    locale
    flags
    exportedTypes
    metadataVersion
    =
    let manifest =
        {
            Name = assemblyName
            AuxModuleHashAlgorithm =
                match hashalg with
                | Some alg -> alg
                | _ -> 0x8004 // SHA1
            SecurityDeclsStored = emptyILSecurityDeclsStored
            PublicKey = None
            Version = None
            Locale = locale
            CustomAttrsStored = storeILCustomAttrs emptyILCustomAttrs
            AssemblyLongevity = ILAssemblyLongevity.Unspecified
            DisableJitOptimizations = 0 <> (flags &&& 0x4000)
            JitTracking = (0 <> (flags &&& 0x8000)) // always turn these on
            IgnoreSymbolStoreSequencePoints = false
            Retargetable = (0 <> (flags &&& 0x100))
            ExportedTypes = exportedTypes
            EntrypointElsewhere = None
            MetadataIndex = NoMetadataIdx
        }

    {
        Manifest = Some manifest
        CustomAttrsStored = storeILCustomAttrs emptyILCustomAttrs
        Name = moduleName
        NativeResources = []
        TypeDefs = tdefs
        SubsystemVersion = subsystemVersion
        UseHighEntropyVA = useHighEntropyVA
        SubSystemFlags = defaultSubSystem
        IsDLL = dll
        IsILOnly = true
        Platform = None
        StackReserveSize = None
        Is32Bit = false
        Is32BitPreferred = false
        Is64Bit = false
        PhysicalAlignment = defaultPhysAlignment
        VirtualAlignment = defaultVirtAlignment
        ImageBase = defaultImageBase
        MetadataVersion = metadataVersion
        Resources = mkILResources []
        MetadataIndex = NoMetadataIdx
    }

//-----------------------------------------------------------------------
// [instructions_to_code] makes the basic block structure of code from
// a primitive array of instructions. We
// do this be iterating over the instructions, pushing new basic blocks
// every time we encounter an address that has been recorded
// [bbstartToCodeLabelMap].
//-----------------------------------------------------------------------

// REVIEW: this function shows up on performance traces. If we eliminated the last ILX->IL rewrites from the
// F# compiler we could get rid of this structured code representation from Abstract IL altogether and
// never convert F# code into this form.
let buildILCode (_methName: string) lab2pc instrs tryspecs localspecs : ILCode =
    {
        Labels = lab2pc
        Instrs = instrs
        Exceptions = tryspecs
        Locals = localspecs
    }

// --------------------------------------------------------------------
// Detecting Delegates
// --------------------------------------------------------------------

let mkILDelegateMethods access (ilg: ILGlobals) (iltyp_AsyncCallback, iltyp_IAsyncResult) (params_, rtv: ILReturn) =
    let retTy = rtv.Type

    let one nm args ret =
        let mdef =
            mkILNonGenericVirtualInstanceMethod (nm, access, args, mkILReturn ret, MethodBody.Abstract)

        mdef.WithAbstract(false).WithHideBySig(true).WithRuntime(true)

    let ctor =
        mkILCtor (
            access,
            [
                mkILParamNamed ("object", ilg.typ_Object)
                mkILParamNamed ("method", ilg.typ_IntPtr)
            ],
            MethodBody.Abstract
        )

    let ctor = ctor.WithRuntime(true).WithHideBySig(true)

    [
        ctor
        one "Invoke" params_ retTy
        one
            "BeginInvoke"
            (params_
             @ [
                 mkILParamNamed ("callback", iltyp_AsyncCallback)
                 mkILParamNamed ("objects", ilg.typ_Object)
             ])
            iltyp_IAsyncResult
        one "EndInvoke" [ mkILParamNamed ("result", iltyp_IAsyncResult) ] retTy
    ]

let mkCtorMethSpecForDelegate (ilg: ILGlobals) (ty: ILType, useUIntPtr) =
    let scoref = ty.TypeRef.Scope

    let argTys =
        [
            rescopeILType scoref ilg.typ_Object
            rescopeILType scoref (if useUIntPtr then ilg.typ_UIntPtr else ilg.typ_IntPtr)
        ]

    mkILInstanceMethSpecInTy (ty, ".ctor", argTys, ILType.Void, emptyILGenericArgsList)

type ILEnumInfo =
    {
        enumValues: (string * ILFieldInit) list
        enumType: ILType
    }

let getTyOfILEnumInfo info = info.enumType

let computeILEnumInfo (mdName, mdFields: ILFieldDefs) =
    match (List.partition (fun (fd: ILFieldDef) -> fd.IsStatic) (mdFields.AsList())) with
    | staticFields, [ vfd ] ->
        {
            enumType = vfd.FieldType
            enumValues =
                staticFields
                |> List.map (fun fd ->
                    (fd.Name,
                     match fd.LiteralValue with
                     | Some i -> i
                     | None ->
                         failwith (
                             "computeILEnumInfo: badly formed enum "
                             + mdName
                             + ": static field does not have an default value"
                         )))
        }
    | _, [] -> failwith ("computeILEnumInfo: badly formed enum " + mdName + ": no non-static field found")
    | _, _ ->
        failwith (
            "computeILEnumInfo: badly formed enum "
            + mdName
            + ": more than one non-static field found"
        )

//---------------------------------------------------------------------
// Primitives to help read signatures. These do not use the file cursor, but
// pass around an int index
//---------------------------------------------------------------------

let sigptr_get_byte bytes sigptr = Bytes.get bytes sigptr, sigptr + 1

let sigptr_get_u8 bytes sigptr =
    let b0, sigptr = sigptr_get_byte bytes sigptr
    byte b0, sigptr

let sigptr_get_i8 bytes sigptr =
    let i, sigptr = sigptr_get_u8 bytes sigptr
    sbyte i, sigptr

let sigptr_get_u16 bytes sigptr =
    let b0, sigptr = sigptr_get_byte bytes sigptr
    let b1, sigptr = sigptr_get_byte bytes sigptr
    uint16 (b0 ||| (b1 <<< 8)), sigptr

let sigptr_get_i16 bytes sigptr =
    let u, sigptr = sigptr_get_u16 bytes sigptr
    int16 u, sigptr

let sigptr_get_i32 bytes sigptr =
    let b0, sigptr = sigptr_get_byte bytes sigptr
    let b1, sigptr = sigptr_get_byte bytes sigptr
    let b2, sigptr = sigptr_get_byte bytes sigptr
    let b3, sigptr = sigptr_get_byte bytes sigptr
    b0 ||| (b1 <<< 8) ||| (b2 <<< 16) ||| (b3 <<< 24), sigptr

let sigptr_get_u32 bytes sigptr =
    let u, sigptr = sigptr_get_i32 bytes sigptr
    uint32 u, sigptr

let sigptr_get_i64 bytes sigptr =
    let b0, sigptr = sigptr_get_byte bytes sigptr
    let b1, sigptr = sigptr_get_byte bytes sigptr
    let b2, sigptr = sigptr_get_byte bytes sigptr
    let b3, sigptr = sigptr_get_byte bytes sigptr
    let b4, sigptr = sigptr_get_byte bytes sigptr
    let b5, sigptr = sigptr_get_byte bytes sigptr
    let b6, sigptr = sigptr_get_byte bytes sigptr
    let b7, sigptr = sigptr_get_byte bytes sigptr

    int64 b0
    ||| (int64 b1 <<< 8)
    ||| (int64 b2 <<< 16)
    ||| (int64 b3 <<< 24)
    ||| (int64 b4 <<< 32)
    ||| (int64 b5 <<< 40)
    ||| (int64 b6 <<< 48)
    ||| (int64 b7 <<< 56),
    sigptr

let sigptr_get_u64 bytes sigptr =
    let u, sigptr = sigptr_get_i64 bytes sigptr
    uint64 u, sigptr

let float32OfBits (x: int32) =
    BitConverter.ToSingle(BitConverter.GetBytes x, 0)

let floatOfBits (x: int64) = BitConverter.Int64BitsToDouble x

let sigptr_get_ieee32 bytes sigptr =
    let u, sigptr = sigptr_get_i32 bytes sigptr
    float32OfBits u, sigptr

let sigptr_get_ieee64 bytes sigptr =
    let u, sigptr = sigptr_get_i64 bytes sigptr
    floatOfBits u, sigptr

let sigptr_get_intarray n (bytes: byte[]) sigptr =
    let res = Bytes.zeroCreate n

    for i = 0 to n - 1 do
        res[i] <- bytes[sigptr + i]

    res, sigptr + n

let sigptr_get_string n bytes sigptr =
    let intarray, sigptr = sigptr_get_intarray n bytes sigptr
    Encoding.UTF8.GetString(intarray, 0, intarray.Length), sigptr

let sigptr_get_z_i32 bytes sigptr =
    let b0, sigptr = sigptr_get_byte bytes sigptr

    if b0 <= 0x7F then
        b0, sigptr
    elif b0 <= 0xbf then
        let b0 = b0 &&& 0x7f
        let b1, sigptr = sigptr_get_byte bytes sigptr
        (b0 <<< 8) ||| b1, sigptr
    else
        let b0 = b0 &&& 0x3f
        let b1, sigptr = sigptr_get_byte bytes sigptr
        let b2, sigptr = sigptr_get_byte bytes sigptr
        let b3, sigptr = sigptr_get_byte bytes sigptr
        (b0 <<< 24) ||| (b1 <<< 16) ||| (b2 <<< 8) ||| b3, sigptr

let sigptr_get_serstring bytes sigptr =
    let len, sigptr = sigptr_get_z_i32 bytes sigptr
    sigptr_get_string len bytes sigptr

let sigptr_get_serstring_possibly_null bytes sigptr =
    let b0, new_sigptr = sigptr_get_byte bytes sigptr

    if b0 = 0xFF then // null case
        None, new_sigptr
    else // throw away new_sigptr, getting length & text advance
        let len, sigptr = sigptr_get_z_i32 bytes sigptr
        let s, sigptr = sigptr_get_string len bytes sigptr
        Some s, sigptr

//---------------------------------------------------------------------
// Get the public key token from the public key.
//---------------------------------------------------------------------

let mkRefToILAssembly (m: ILAssemblyManifest) =
    ILAssemblyRef.Create(
        m.Name,
        None,
        (match m.PublicKey with
         | Some k -> Some(PublicKey.KeyAsToken k)
         | None -> None),
        m.Retargetable,
        m.Version,
        m.Locale
    )

let z_unsigned_int n =
    if n >= 0 && n <= 0x7F then
        [| byte n |]
    elif n >= 0x80 && n <= 0x3FFF then
        [| byte (0x80 ||| (n >>>& 8)); byte (n &&& 0xFF) |]
    else
        [|
            byte (0xc0 ||| (n >>>& 24))
            byte ((n >>>& 16) &&& 0xFF)
            byte ((n >>>& 8) &&& 0xFF)
            byte (n &&& 0xFF)
        |]

let string_as_utf8_bytes (s: string) = Encoding.UTF8.GetBytes s

(* Little-endian encoding of int64 *)
let dw7 n = byte ((n >>> 56) &&& 0xFFL)

let dw6 n = byte ((n >>> 48) &&& 0xFFL)

let dw5 n = byte ((n >>> 40) &&& 0xFFL)

let dw4 n = byte ((n >>> 32) &&& 0xFFL)

let dw3 n = byte ((n >>> 24) &&& 0xFFL)

let dw2 n = byte ((n >>> 16) &&& 0xFFL)

let dw1 n = byte ((n >>> 8) &&& 0xFFL)

let dw0 n = byte (n &&& 0xFFL)

let u8AsBytes (i: byte) = [| i |]

let u16AsBytes x =
    let n = (int x) in [| byte (b0 n); byte (b1 n) |]

let i32AsBytes i =
    [| byte (b0 i); byte (b1 i); byte (b2 i); byte (b3 i) |]

let i64AsBytes i =
    [| dw0 i; dw1 i; dw2 i; dw3 i; dw4 i; dw5 i; dw6 i; dw7 i |]

let i8AsBytes (i: sbyte) = u8AsBytes (byte i)

let i16AsBytes (i: int16) = u16AsBytes (uint16 i)

let u32AsBytes (i: uint32) = i32AsBytes (int32 i)

let u64AsBytes (i: uint64) = i64AsBytes (int64 i)

let bitsOfSingle (x: float32) =
    BitConverter.ToInt32(BitConverter.GetBytes x, 0)

let bitsOfDouble (x: float) = BitConverter.DoubleToInt64Bits x

let ieee32AsBytes i = i32AsBytes (bitsOfSingle i)

let ieee64AsBytes i = i64AsBytes (bitsOfDouble i)

let et_BOOLEAN = 0x02uy
let et_CHAR = 0x03uy
let et_I1 = 0x04uy
let et_U1 = 0x05uy
let et_I2 = 0x06uy
let et_U2 = 0x07uy
let et_I4 = 0x08uy
let et_U4 = 0x09uy
let et_I8 = 0x0Auy
let et_U8 = 0x0Buy
let et_R4 = 0x0Cuy
let et_R8 = 0x0Duy
let et_STRING = 0x0Euy
let et_OBJECT = 0x1Cuy
let et_SZARRAY = 0x1Duy

let formatILVersion (version: ILVersionInfo) =
    sprintf "%d.%d.%d.%d" (int version.Major) (int version.Minor) (int version.Build) (int version.Revision)

let encodeCustomAttrString s =
    let arr = string_as_utf8_bytes s
    Array.append (z_unsigned_int arr.Length) arr

let rec encodeCustomAttrElemType x =
    match x with
    | ILType.Value tspec when tspec.Name = tname_SByte -> [| et_I1 |]
    | ILType.Value tspec when tspec.Name = tname_Byte -> [| et_U1 |]
    | ILType.Value tspec when tspec.Name = tname_Int16 -> [| et_I2 |]
    | ILType.Value tspec when tspec.Name = tname_UInt16 -> [| et_U2 |]
    | ILType.Value tspec when tspec.Name = tname_Int32 -> [| et_I4 |]
    | ILType.Value tspec when tspec.Name = tname_UInt32 -> [| et_U4 |]
    | ILType.Value tspec when tspec.Name = tname_Int64 -> [| et_I8 |]
    | ILType.Value tspec when tspec.Name = tname_UInt64 -> [| et_U8 |]
    | ILType.Value tspec when tspec.Name = tname_Double -> [| et_R8 |]
    | ILType.Value tspec when tspec.Name = tname_Single -> [| et_R4 |]
    | ILType.Value tspec when tspec.Name = tname_Char -> [| et_CHAR |]
    | ILType.Value tspec when tspec.Name = tname_Bool -> [| et_BOOLEAN |]
    | ILType.Boxed tspec when tspec.Name = tname_String -> [| et_STRING |]
    | ILType.Boxed tspec when tspec.Name = tname_Object -> [| 0x51uy |]
    | ILType.Boxed tspec when tspec.Name = tname_Type -> [| 0x50uy |]
    | ILType.Value tspec -> Array.append [| 0x55uy |] (encodeCustomAttrString tspec.TypeRef.QualifiedName)
    | ILType.Array(shape, elemType) when shape = ILArrayShape.SingleDimensional ->
        Array.append [| et_SZARRAY |] (encodeCustomAttrElemType elemType)
    | _ -> failwith "encodeCustomAttrElemType: unrecognized custom element type"

/// Given a custom attribute element, work out the type of the .NET argument for that element.
let rec encodeCustomAttrElemTypeForObject x =
    match x with
    | ILAttribElem.String _ -> [| et_STRING |]
    | ILAttribElem.Bool _ -> [| et_BOOLEAN |]
    | ILAttribElem.Char _ -> [| et_CHAR |]
    | ILAttribElem.SByte _ -> [| et_I1 |]
    | ILAttribElem.Int16 _ -> [| et_I2 |]
    | ILAttribElem.Int32 _ -> [| et_I4 |]
    | ILAttribElem.Int64 _ -> [| et_I8 |]
    | ILAttribElem.Byte _ -> [| et_U1 |]
    | ILAttribElem.UInt16 _ -> [| et_U2 |]
    | ILAttribElem.UInt32 _ -> [| et_U4 |]
    | ILAttribElem.UInt64 _ -> [| et_U8 |]
    | ILAttribElem.Type _ -> [| 0x50uy |]
    | ILAttribElem.TypeRef _ -> [| 0x50uy |]
    | ILAttribElem.Null -> [| et_STRING |] // yes, the 0xe prefix is used when passing a "null" to a property or argument of type "object" here
    | ILAttribElem.Single _ -> [| et_R4 |]
    | ILAttribElem.Double _ -> [| et_R8 |]
    | ILAttribElem.Array(elemTy, _) -> [| yield et_SZARRAY; yield! encodeCustomAttrElemType elemTy |]

let tspan = TimeSpan(DateTime.UtcNow.Ticks - DateTime(2000, 1, 1).Ticks)

let parseILVersion (vstr: string) =
    // matches "v1.2.3.4" or "1.2.3.4". Note, if numbers are missing, returns -1 (not 0).
    let mutable vstr = vstr.TrimStart [| 'v' |]
    // if the version string contains wildcards, replace them
    let versionComponents = vstr.Split [| '.' |]

    // account for wildcards
    if versionComponents.Length > 2 then
        let defaultBuild = uint16 tspan.Days % UInt16.MaxValue - 1us

        let defaultRevision =
            uint16 (DateTime.UtcNow.TimeOfDay.TotalSeconds / 2.0) % UInt16.MaxValue - 1us

        if versionComponents[2] = "*" then
            if versionComponents.Length > 3 then
                failwith "Invalid version format"
            else
                // set the build number to the number of days since Jan 1, 2000
                versionComponents[2] <- defaultBuild.ToString()
                // Set the revision number to number of seconds today / 2
                vstr <- String.Join(".", versionComponents) + "." + defaultRevision.ToString()
        elif versionComponents.Length > 3 && versionComponents[3] = "*" then
            // Set the revision number to number of seconds today / 2
            versionComponents[3] <- defaultRevision.ToString()
            vstr <- String.Join(".", versionComponents)

    let version = Version vstr
    let zero32 n = if n < 0 then 0us else uint16 n
    // since the minor revision will be -1 if none is specified, we need to truncate to 0 to not break existing code
    let minorRevision =
        if version.Revision = -1 then
            0us
        else
            uint16 version.MinorRevision

    ILVersionInfo(zero32 version.Major, zero32 version.Minor, zero32 version.Build, minorRevision)

let compareILVersions (version1: ILVersionInfo) (version2: ILVersionInfo) =
    let c = compare version1.Major version2.Major

    if c <> 0 then
        c
    else
        let c = compare version1.Minor version2.Minor

        if c <> 0 then
            c
        else
            let c = compare version1.Build version2.Build

            if c <> 0 then
                c
            else
                let c = compare version1.Revision version2.Revision
                if c <> 0 then c else 0

let DummyFSharpCoreScopeRef =
    let asmRef =
        // The exact public key token and version used here don't actually matter, or shouldn't.
        ILAssemblyRef.Create(
            "FSharp.Core",
            None,
            Some(PublicKeyToken(Bytes.ofInt32Array [| 0xb0; 0x3f; 0x5f; 0x7f; 0x11; 0xd5; 0x0a; 0x3a |])),
            false,
            Some(parseILVersion "0.0.0.0"),
            None
        )

    ILScopeRef.Assembly asmRef

let PrimaryAssemblyILGlobals =
    mkILGlobals (ILScopeRef.PrimaryAssembly, [], DummyFSharpCoreScopeRef)

let rec decodeCustomAttrElemType bytes sigptr x =
    match x with
    | x when x = et_I1 -> PrimaryAssemblyILGlobals.typ_SByte, sigptr
    | x when x = et_U1 -> PrimaryAssemblyILGlobals.typ_Byte, sigptr
    | x when x = et_I2 -> PrimaryAssemblyILGlobals.typ_Int16, sigptr
    | x when x = et_U2 -> PrimaryAssemblyILGlobals.typ_UInt16, sigptr
    | x when x = et_I4 -> PrimaryAssemblyILGlobals.typ_Int32, sigptr
    | x when x = et_U4 -> PrimaryAssemblyILGlobals.typ_UInt32, sigptr
    | x when x = et_I8 -> PrimaryAssemblyILGlobals.typ_Int64, sigptr
    | x when x = et_U8 -> PrimaryAssemblyILGlobals.typ_UInt64, sigptr
    | x when x = et_R8 -> PrimaryAssemblyILGlobals.typ_Double, sigptr
    | x when x = et_R4 -> PrimaryAssemblyILGlobals.typ_Single, sigptr
    | x when x = et_CHAR -> PrimaryAssemblyILGlobals.typ_Char, sigptr
    | x when x = et_BOOLEAN -> PrimaryAssemblyILGlobals.typ_Bool, sigptr
    | x when x = et_STRING -> PrimaryAssemblyILGlobals.typ_String, sigptr
    | x when x = et_OBJECT -> PrimaryAssemblyILGlobals.typ_Object, sigptr
    | x when x = et_SZARRAY ->
        let et, sigptr = sigptr_get_u8 bytes sigptr
        let elemTy, sigptr = decodeCustomAttrElemType bytes sigptr et
        mkILArr1DTy elemTy, sigptr
    | x when x = 0x50uy -> PrimaryAssemblyILGlobals.typ_Type, sigptr
    | _ -> failwithf "decodeCustomAttrElemType ilg: unrecognized custom element type: %A" x

/// Given a custom attribute element, encode it to a binary representation according to the rules in Ecma 335 Partition II.
let rec encodeCustomAttrPrimValue c =
    match c with
    | ILAttribElem.Bool b -> [| (if b then 0x01uy else 0x00uy) |]
    | ILAttribElem.String None
    | ILAttribElem.Type None
    | ILAttribElem.TypeRef None
    | ILAttribElem.Null -> [| 0xFFuy |]
    | ILAttribElem.String(Some s) -> encodeCustomAttrString s
    | ILAttribElem.Char x -> u16AsBytes (uint16 x)
    | ILAttribElem.SByte x -> i8AsBytes x
    | ILAttribElem.Int16 x -> i16AsBytes x
    | ILAttribElem.Int32 x -> i32AsBytes x
    | ILAttribElem.Int64 x -> i64AsBytes x
    | ILAttribElem.Byte x -> u8AsBytes x
    | ILAttribElem.UInt16 x -> u16AsBytes x
    | ILAttribElem.UInt32 x -> u32AsBytes x
    | ILAttribElem.UInt64 x -> u64AsBytes x
    | ILAttribElem.Single x -> ieee32AsBytes x
    | ILAttribElem.Double x -> ieee64AsBytes x
    | ILAttribElem.Type(Some ty) -> encodeCustomAttrString ty.QualifiedName
    | ILAttribElem.TypeRef(Some tref) -> encodeCustomAttrString tref.QualifiedName
    | ILAttribElem.Array(_, elems) ->
        [|
            yield! i32AsBytes elems.Length
            for elem in elems do
                yield! encodeCustomAttrPrimValue elem
        |]

and encodeCustomAttrValue ty c =
    match ty, c with
    | ILType.Boxed tspec, _ when tspec.Name = tname_Object ->
        [|
            yield! encodeCustomAttrElemTypeForObject c
            yield! encodeCustomAttrPrimValue c
        |]
    | ILType.Array(shape, _), ILAttribElem.Null when shape = ILArrayShape.SingleDimensional -> [| yield! i32AsBytes 0xFFFFFFFF |]
    | ILType.Array(shape, elemType), ILAttribElem.Array(_, elems) when shape = ILArrayShape.SingleDimensional ->
        [|
            yield! i32AsBytes elems.Length
            for elem in elems do
                yield! encodeCustomAttrValue elemType elem
        |]
    | _ -> encodeCustomAttrPrimValue c

let encodeCustomAttrNamedArg (nm, ty, prop, elem) =
    [|
        yield (if prop then 0x54uy else 0x53uy)
        yield! encodeCustomAttrElemType ty
        yield! encodeCustomAttrString nm
        yield! encodeCustomAttrValue ty elem
    |]

let encodeCustomAttrArgs (mspec: ILMethodSpec) (fixedArgs: _ list) (namedArgs: _ list) =
    let argTys = mspec.MethodRef.ArgTypes

    [|
        yield! [| 0x01uy; 0x00uy |]
        for argTy, fixedArg in Seq.zip argTys fixedArgs do
            yield! encodeCustomAttrValue argTy fixedArg
        yield! u16AsBytes (uint16 namedArgs.Length)
        for namedArg in namedArgs do
            yield! encodeCustomAttrNamedArg namedArg
    |]

let encodeCustomAttr (mspec: ILMethodSpec, fixedArgs, namedArgs) =
    let args = encodeCustomAttrArgs mspec fixedArgs namedArgs
    ILAttribute.Encoded(mspec, args, fixedArgs @ (namedArgs |> List.map (fun (_, _, _, e) -> e)))

let mkILCustomAttribMethRef (mspec: ILMethodSpec, fixedArgs, namedArgs) =
    encodeCustomAttr (mspec, fixedArgs, namedArgs)

let mkILCustomAttribute (tref, argTys, argvs, propvs) =
    encodeCustomAttr (mkILNonGenericCtorMethSpec (tref, argTys), argvs, propvs)

let getCustomAttrData cattr =
    match cattr with
    | ILAttribute.Encoded(_, data, _) -> data
    | ILAttribute.Decoded(mspec, fixedArgs, namedArgs) -> encodeCustomAttrArgs mspec fixedArgs namedArgs

// ILSecurityDecl is a 'blob' having the following format:
// - A byte containing a period (.).
// - A compressed int32 containing the number of attributes encoded in the blob.
// - An array of attributes each containing the following:
// - A String, which is the fully-qualified type name of the attribute. (Strings are encoded
//      as a compressed int to indicate the size followed by an array of UTF8 characters.)
// - A set of properties, encoded as the named arguments to a custom attribute would be (as
//      in §23.3, beginning with NumNamed).
let mkPermissionSet (action, attributes: (ILTypeRef * (string * ILType * ILAttribElem) list) list) =
    let bytes =
        [|
            yield (byte '.')
            yield! z_unsigned_int attributes.Length
            for tref: ILTypeRef, props in attributes do
                yield! encodeCustomAttrString tref.QualifiedName

                let bytes =
                    [|
                        yield! z_unsigned_int props.Length
                        for nm, ty, value in props do
                            yield! encodeCustomAttrNamedArg (nm, ty, true, value)
                    |]

                yield! z_unsigned_int bytes.Length
                yield! bytes
        |]

    ILSecurityDecl.ILSecurityDecl(action, bytes)

// Parse an IL type signature argument within a custom attribute blob
type ILTypeSigParser(tstring: string) =

    let mutable startPos = 0
    let mutable currentPos = 0

    let reset () =
        startPos <- 0
        currentPos <- 0

    let nil = '\r' // cannot appear in a type sig

    // take a look at the next value, but don't advance
    let peek () =
        if currentPos < (tstring.Length - 1) then
            tstring[currentPos + 1]
        else
            nil

    let peekN skip =
        if currentPos < (tstring.Length - skip) then
            tstring[currentPos + skip]
        else
            nil
    // take a look at the current value, but don't advance
    let here () =
        if currentPos < tstring.Length then
            tstring[currentPos]
        else
            nil
    // move on to the next character
    let step () = currentPos <- currentPos + 1
    // ignore the current lexeme
    let skip () = startPos <- currentPos
    // ignore the current lexeme, advance
    let drop () =
        skip ()
        step ()
        skip ()
    // return the current lexeme, advance
    let take () =
        let s =
            if currentPos < tstring.Length then
                tstring[startPos..currentPos]
            else
                ""

        drop ()
        s

    // The format we accept is
    // "<type name>{`<arity>[<type>, +]}{<array rank>}{<scope>}" E.g.,
    //
    // System.Collections.Generic.Dictionary
    //     `2[
    //         [System.Int32, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089],
    //         dev.virtualearth.net.webservices.v1.search.CategorySpecificPropertySet],
    // mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"
    //
    // Note that
    //   Since we're only reading valid IL, we assume that the signature is properly formed
    //   For type parameters, if the type is non-local, it will be wrapped in brackets ([])
    //   Still needs testing with jagged arrays and byref parameters
    member private x.ParseType() =

        // Does the type name start with a leading '['? If so, ignore it
        // (if the specialization type is in another module, it will be wrapped in bracket)
        if here () = '[' then
            drop ()

        // 1. Iterate over beginning of type, grabbing the type name and determining if it's generic or an array
        let typeName =
            while (peek () <> '`')
                  && (peek () <> '[')
                  && (peek () <> ']')
                  && (peek () <> ',')
                  && (peek () <> nil) do
                step ()

            take ()

        // 2. Classify the type

        // Is the type generic?
        let typeName, specializations =
            if here () = '`' then
                drop () // step to the number
                // fetch the arity
                let arity =
                    while (int (here ()) >= (int ('0')))
                          && (int (here ()) <= (int ('9')))
                          && (int (peek ()) >= (int ('0')))
                          && (int (peek ()) <= (int ('9'))) do
                        step ()

                    Int32.Parse(take ())
                // skip the '['
                drop ()
                // get the specializations
                typeName + "`" + (arity.ToString()),
                Some
                    [
                        for _i in 0 .. arity - 1 do
                            yield x.ParseType()
                    ]
            else
                typeName, None

        // Is the type an array?
        let rank =
            if here () = '[' then
                let mutable rank = 0

                while here () <> ']' do
                    rank <- rank + 1
                    step ()

                drop ()

                Some(ILArrayShape(List.replicate rank (Some 0, None)))
            else
                None

        // Is there a scope?
        let scope =
            if (here () = ',' || here () = ' ') && (peek () <> '[' && peekN 2 <> '[') then
                let grabScopeComponent () =
                    if here () = ',' then
                        drop () // ditch the ','

                    if here () = ' ' then
                        drop () // ditch the ' '

                    while (peek () <> ',' && peek () <> ']' && peek () <> nil) do
                        step ()

                    take ()

                let scope =
                    [
                        yield grabScopeComponent () // assembly
                        yield grabScopeComponent () // version
                        yield grabScopeComponent () // culture
                        yield grabScopeComponent () // public key token
                    ]
                    |> String.concat ","

                ILScopeRef.Assembly(ILAssemblyRef.FromAssemblyName(AssemblyName scope))
            else
                ILScopeRef.Local

        // strip any extraneous trailing brackets or commas
        if (here () = ']') then
            drop ()

        if (here () = ',') then
            drop ()

        // build the IL type
        let tref = mkILTyRef (scope, typeName)

        let genericArgs =
            match specializations with
            | None -> []
            | Some genericArgs -> genericArgs

        let tspec = ILTypeSpec.Create(tref, genericArgs)

        let ilTy =
            match tspec.Name with
            | "System.SByte"
            | "System.Byte"
            | "System.Int16"
            | "System.UInt16"
            | "System.Int32"
            | "System.UInt32"
            | "System.Int64"
            | "System.UInt64"
            | "System.Char"
            | "System.Double"
            | "System.Single"
            | "System.Boolean" -> ILType.Value tspec
            | _ -> ILType.Boxed tspec

        // if it's an array, wrap it - otherwise, just return the IL type
        match rank with
        | Some r -> ILType.Array(r, ilTy)
        | _ -> ilTy

    member x.ParseTypeSpec() =
        reset ()
        let ilTy = x.ParseType()
        ILAttribElem.Type(Some ilTy)

let decodeILAttribData (ca: ILAttribute) =
    match ca with
    | ILAttribute.Decoded(_, fixedArgs, namedArgs) -> fixedArgs, namedArgs
    | ILAttribute.Encoded(_, bytes, _) ->

        let sigptr = 0
        let bb0, sigptr = sigptr_get_byte bytes sigptr
        let bb1, sigptr = sigptr_get_byte bytes sigptr

        if not (bb0 = 0x01 && bb1 = 0x00) then
            failwith "decodeILAttribData: invalid data"

        let rec parseVal argTy sigptr =
            match argTy with
            | ILType.Value tspec when tspec.Name = "System.SByte" ->
                let n, sigptr = sigptr_get_i8 bytes sigptr
                ILAttribElem.SByte n, sigptr
            | ILType.Value tspec when tspec.Name = "System.Byte" ->
                let n, sigptr = sigptr_get_u8 bytes sigptr
                ILAttribElem.Byte n, sigptr
            | ILType.Value tspec when tspec.Name = "System.Int16" ->
                let n, sigptr = sigptr_get_i16 bytes sigptr
                ILAttribElem.Int16 n, sigptr
            | ILType.Value tspec when tspec.Name = "System.UInt16" ->
                let n, sigptr = sigptr_get_u16 bytes sigptr
                ILAttribElem.UInt16 n, sigptr
            | ILType.Value tspec when tspec.Name = "System.Int32" ->
                let n, sigptr = sigptr_get_i32 bytes sigptr
                ILAttribElem.Int32 n, sigptr
            | ILType.Value tspec when tspec.Name = "System.UInt32" ->
                let n, sigptr = sigptr_get_u32 bytes sigptr
                ILAttribElem.UInt32 n, sigptr
            | ILType.Value tspec when tspec.Name = "System.Int64" ->
                let n, sigptr = sigptr_get_i64 bytes sigptr
                ILAttribElem.Int64 n, sigptr
            | ILType.Value tspec when tspec.Name = "System.UInt64" ->
                let n, sigptr = sigptr_get_u64 bytes sigptr
                ILAttribElem.UInt64 n, sigptr
            | ILType.Value tspec when tspec.Name = "System.Double" ->
                let n, sigptr = sigptr_get_ieee64 bytes sigptr
                ILAttribElem.Double n, sigptr
            | ILType.Value tspec when tspec.Name = "System.Single" ->
                let n, sigptr = sigptr_get_ieee32 bytes sigptr
                ILAttribElem.Single n, sigptr
            | ILType.Value tspec when tspec.Name = "System.Char" ->
                let n, sigptr = sigptr_get_u16 bytes sigptr
                ILAttribElem.Char(char (int32 n)), sigptr
            | ILType.Value tspec when tspec.Name = "System.Boolean" ->
                let n, sigptr = sigptr_get_byte bytes sigptr
                ILAttribElem.Bool(n <> 0), sigptr
            | ILType.Boxed tspec when tspec.Name = "System.String" ->
                let n, sigptr = sigptr_get_serstring_possibly_null bytes sigptr
                ILAttribElem.String n, sigptr
            | ILType.Boxed tspec when tspec.Name = "System.Type" ->
                let nOpt, sigptr = sigptr_get_serstring_possibly_null bytes sigptr

                match nOpt with
                | None -> ILAttribElem.TypeRef None, sigptr
                | Some n ->
                    try
                        let parser = ILTypeSigParser n
                        parser.ParseTypeSpec(), sigptr
                    with exn ->
                        failwith (sprintf "decodeILAttribData: error parsing type in custom attribute blob: %s" exn.Message)
            | ILType.Boxed tspec when tspec.Name = "System.Object" ->
                let et, sigptr = sigptr_get_u8 bytes sigptr

                if et = 0xFFuy then
                    ILAttribElem.Null, sigptr
                else
                    let ty, sigptr = decodeCustomAttrElemType bytes sigptr et
                    parseVal ty sigptr
            | ILType.Array(shape, elemTy) when shape = ILArrayShape.SingleDimensional ->
                let n, sigptr = sigptr_get_i32 bytes sigptr

                if n = 0xFFFFFFFF then
                    ILAttribElem.Null, sigptr
                else
                    let rec parseElems acc n sigptr =
                        if n = 0 then
                            List.rev acc, sigptr
                        else
                            let v, sigptr = parseVal elemTy sigptr
                            parseElems (v :: acc) (n - 1) sigptr

                    let elems, sigptr = parseElems [] n sigptr
                    ILAttribElem.Array(elemTy, elems), sigptr
            | ILType.Value _ -> (* assume it is an enumeration *)
                let n, sigptr = sigptr_get_i32 bytes sigptr
                ILAttribElem.Int32 n, sigptr
            | _ -> failwith "decodeILAttribData: attribute data involves an enum or System.Type value"

        let rec parseFixed argTys sigptr =
            match argTys with
            | [] -> [], sigptr
            | h :: t ->
                let nh, sigptr = parseVal h sigptr
                let nt, sigptr = parseFixed t sigptr
                nh :: nt, sigptr

        let fixedArgs, sigptr = parseFixed ca.Method.FormalArgTypes sigptr
        let nnamed, sigptr = sigptr_get_u16 bytes sigptr

        let rec parseNamed acc n sigptr =
            if n = 0 then
                List.rev acc
            else
                let isPropByte, sigptr = sigptr_get_u8 bytes sigptr
                let isProp = (int isPropByte = 0x54)
                let et, sigptr = sigptr_get_u8 bytes sigptr
                // We have a named value
                let ty, sigptr =
                    if ( (* 0x50 = (int et) || *) 0x55 = (int et)) then
                        let qualified_tname, sigptr = sigptr_get_serstring bytes sigptr

                        let unqualified_tname, rest =
                            let pieces = qualified_tname.Split ','

                            if pieces.Length > 1 then
                                pieces[0], Some(String.concat "," pieces[1..])
                            else
                                pieces[0], None

                        let scoref =
                            match rest with
                            | Some aname -> ILScopeRef.Assembly(ILAssemblyRef.FromAssemblyName(AssemblyName aname))
                            | None -> PrimaryAssemblyILGlobals.primaryAssemblyScopeRef

                        let tref = mkILTyRef (scoref, unqualified_tname)
                        let tspec = mkILNonGenericTySpec tref
                        ILType.Value tspec, sigptr
                    else
                        decodeCustomAttrElemType bytes sigptr et

                let nm, sigptr = sigptr_get_serstring bytes sigptr
                let v, sigptr = parseVal ty sigptr
                parseNamed ((nm, ty, isProp, v) :: acc) (n - 1) sigptr

        let named = parseNamed [] (int nnamed) sigptr
        fixedArgs, named

// --------------------------------------------------------------------
// Functions to collect up all the references in a full module or
// assembly manifest. The process also allocates
// a unique name to each unique internal assembly reference.
// --------------------------------------------------------------------

type ILReferences =
    {
        AssemblyReferences: ILAssemblyRef[]
        ModuleReferences: ILModuleRef[]
        TypeReferences: ILTypeRef[]
        MethodReferences: ILMethodRef[]
        FieldReferences: ILFieldRef[]
    }

type ILReferencesAccumulator =
    {
        ilg: ILGlobals
        refsA: HashSet<ILAssemblyRef>
        refsM: HashSet<ILModuleRef>
        refsTs: HashSet<ILTypeRef>
        refsMs: HashSet<ILMethodRef>
        refsFs: HashSet<ILFieldRef>
    }

let emptyILRefs =
    {
        AssemblyReferences = [||]
        ModuleReferences = [||]
        TypeReferences = [||]
        MethodReferences = [||]
        FieldReferences = [||]
    }

let refsOfILAssemblyRef (s: ILReferencesAccumulator) x = s.refsA.Add x |> ignore

let refsOfILModuleRef (s: ILReferencesAccumulator) x = s.refsM.Add x |> ignore

let refsOfScopeRef s x =
    match x with
    | ILScopeRef.Local -> ()
    | ILScopeRef.Assembly assemblyRef -> refsOfILAssemblyRef s assemblyRef
    | ILScopeRef.Module modref -> refsOfILModuleRef s modref
    | ILScopeRef.PrimaryAssembly -> refsOfILAssemblyRef s s.ilg.primaryAssemblyRef

let refsOfILTypeRef s (x: ILTypeRef) = refsOfScopeRef s x.Scope

let rec refsOfILType s x =
    match x with
    | ILType.Void
    | ILType.TypeVar _ -> ()
    | ILType.Modified(_, ty1, ty2) ->
        refsOfILTypeRef s ty1
        refsOfILType s ty2
    | ILType.Array(_, ty)
    | ILType.Ptr ty
    | ILType.Byref ty -> refsOfILType s ty
    | ILType.Value tr
    | ILType.Boxed tr -> refsOfILTypeSpec s tr
    | ILType.FunctionPointer mref -> refsOfILCallsig s mref

and refsOfILTypeSpec s (x: ILTypeSpec) =
    refsOfILTypeRef s x.TypeRef
    refsOfILTypes s x.GenericArgs

and refsOfILCallsig s csig =
    refsOfILTypes s csig.ArgTypes
    refsOfILType s csig.ReturnType

and refsOfILGenericParam s x = refsOfILTypes s x.Constraints

and refsOfILGenericParams s b = List.iter (refsOfILGenericParam s) b

and refsOfILMethodRef s (x: ILMethodRef) =
    refsOfILTypeRef s x.DeclaringTypeRef
    refsOfILTypes s x.mrefArgs
    refsOfILType s x.mrefReturn
    s.refsMs.Add x |> ignore

and refsOfILFieldRef s x =
    refsOfILTypeRef s x.DeclaringTypeRef
    refsOfILType s x.Type
    s.refsFs.Add x |> ignore

and refsOfILOverridesSpec s (OverridesSpec(mref, ty)) =
    refsOfILMethodRef s mref
    refsOfILType s ty

and refsOfILMethodSpec s (x: ILMethodSpec) =
    refsOfILMethodRef s x.MethodRef
    refsOfILType s x.DeclaringType
    refsOfILTypes s x.GenericArgs

and refsOfILFieldSpec s x =
    refsOfILFieldRef s x.FieldRef
    refsOfILType s x.DeclaringType

and refsOfILTypes s l = List.iter (refsOfILType s) l

and refsOfILToken s x =
    match x with
    | ILToken.ILType ty -> refsOfILType s ty
    | ILToken.ILMethod mr -> refsOfILMethodSpec s mr
    | ILToken.ILField fr -> refsOfILFieldSpec s fr

and refsOfILCustomAttrElem s (elem: ILAttribElem) =
    match elem with
    | Type(Some ty) -> refsOfILType s ty
    | TypeRef(Some tref) -> refsOfILTypeRef s tref
    | Array(ty, els) ->
        refsOfILType s ty
        refsOfILCustomAttrElems s els
    | _ -> ()

and refsOfILCustomAttrElems s els =
    els |> List.iter (refsOfILCustomAttrElem s)

and refsOfILCustomAttr s (cattr: ILAttribute) =
    refsOfILMethodSpec s cattr.Method
    refsOfILCustomAttrElems s cattr.Elements

and refsOfILCustomAttrs s (cas: ILAttributes) =
    cas.AsArray() |> Array.iter (refsOfILCustomAttr s)

and refsOfILVarArgs s tyso = Option.iter (refsOfILTypes s) tyso

and refsOfILInstr s x =
    match x with
    | I_call(_, mr, varargs)
    | I_newobj(mr, varargs)
    | I_callvirt(_, mr, varargs) ->
        refsOfILMethodSpec s mr
        refsOfILVarArgs s varargs
    | I_callconstraint(_, _, tr, mr, varargs) ->
        refsOfILType s tr
        refsOfILMethodSpec s mr
        refsOfILVarArgs s varargs
    | I_calli(_, callsig, varargs) ->
        refsOfILCallsig s callsig
        refsOfILVarArgs s varargs
    | I_jmp mr
    | I_ldftn mr
    | I_ldvirtftn mr -> refsOfILMethodSpec s mr
    | I_ldsfld(_, fr)
    | I_ldfld(_, _, fr)
    | I_ldsflda fr
    | I_ldflda fr
    | I_stsfld(_, fr)
    | I_stfld(_, _, fr) -> refsOfILFieldSpec s fr
    | I_isinst ty
    | I_castclass ty
    | I_cpobj ty
    | I_initobj ty
    | I_ldobj(_, _, ty)
    | I_stobj(_, _, ty)
    | I_box ty
    | I_unbox ty
    | I_unbox_any ty
    | I_sizeof ty
    | I_ldelem_any(_, ty)
    | I_ldelema(_, _, _, ty)
    | I_stelem_any(_, ty)
    | I_newarr(_, ty)
    | I_mkrefany ty
    | I_refanyval ty
    | EI_ilzero ty -> refsOfILType s ty
    | I_ldtoken token -> refsOfILToken s token
    | I_stelem _
    | I_ldelem _
    | I_ldstr _
    | I_switch _
    | I_stloc _
    | I_stind _
    | I_starg _
    | I_ldloca _
    | I_ldloc _
    | I_ldind _
    | I_ldarga _
    | I_ldarg _
    | I_leave _
    | I_br _
    | I_brcmp _
    | I_rethrow
    | I_refanytype
    | I_ldlen
    | I_throw
    | I_initblk _
    | I_cpblk _
    | I_localloc
    | I_ret
    | I_endfilter
    | I_endfinally
    | I_arglist
    | I_break
    | AI_add
    | AI_add_ovf
    | AI_add_ovf_un
    | AI_and
    | AI_div
    | AI_div_un
    | AI_ceq
    | AI_cgt
    | AI_cgt_un
    | AI_clt
    | AI_clt_un
    | AI_conv _
    | AI_conv_ovf _
    | AI_conv_ovf_un _
    | AI_mul
    | AI_mul_ovf
    | AI_mul_ovf_un
    | AI_rem
    | AI_rem_un
    | AI_shl
    | AI_shr
    | AI_shr_un
    | AI_sub
    | AI_sub_ovf
    | AI_sub_ovf_un
    | AI_xor
    | AI_or
    | AI_neg
    | AI_not
    | AI_ldnull
    | AI_dup
    | AI_pop
    | AI_ckfinite
    | AI_nop
    | AI_ldc _
    | I_seqpoint _
    | EI_ldlen_multi _ -> ()

and refsOfILCode s (c: ILCode) =
    for i in c.Instrs do
        refsOfILInstr s i

    for exnClause in c.Exceptions do
        match exnClause.Clause with
        | ILExceptionClause.TypeCatch(ilTy, _) -> refsOfILType s ilTy
        | _ -> ()

and refsOfILMethodBody s (il: ILMethodBody) =
    List.iter (refsOfILLocal s) il.Locals
    refsOfILCode s il.Code

and refsOfILLocal s loc = refsOfILType s loc.Type

and refsOfMethodBody s x =
    match x with
    | MethodBody.IL il -> refsOfILMethodBody s il.Value
    | MethodBody.PInvoke attr -> refsOfILModuleRef s attr.Value.Where
    | _ -> ()

and refsOfILMethodDef s (md: ILMethodDef) =
    List.iter (refsOfILParam s) md.Parameters
    refsOfILReturn s md.Return
    refsOfMethodBody s md.Body
    refsOfILCustomAttrs s md.CustomAttrs
    refsOfILGenericParams s md.GenericParams

and refsOfILParam s p = refsOfILType s p.Type

and refsOfILReturn s (rt: ILReturn) = refsOfILType s rt.Type

and refsOfILMethodDefs s x = Seq.iter (refsOfILMethodDef s) x

and refsOfILEventDef s (ed: ILEventDef) =
    Option.iter (refsOfILType s) ed.EventType
    refsOfILMethodRef s ed.AddMethod
    refsOfILMethodRef s ed.RemoveMethod
    Option.iter (refsOfILMethodRef s) ed.FireMethod
    List.iter (refsOfILMethodRef s) ed.OtherMethods
    refsOfILCustomAttrs s ed.CustomAttrs

and refsOfILEventDefs s (x: ILEventDefs) =
    List.iter (refsOfILEventDef s) (x.AsList())

and refsOfILPropertyDef s (pd: ILPropertyDef) =
    Option.iter (refsOfILMethodRef s) pd.SetMethod
    Option.iter (refsOfILMethodRef s) pd.GetMethod
    refsOfILType s pd.PropertyType
    refsOfILTypes s pd.Args
    refsOfILCustomAttrs s pd.CustomAttrs

and refsOfILPropertyDefs s (x: ILPropertyDefs) =
    List.iter (refsOfILPropertyDef s) (x.AsList())

and refsOfILFieldDef s (fd: ILFieldDef) =
    refsOfILType s fd.FieldType
    refsOfILCustomAttrs s fd.CustomAttrs

and refsOfILFieldDefs s fields = List.iter (refsOfILFieldDef s) fields

and refsOfILMethodImpls s mimpls = List.iter (refsOfILMethodImpl s) mimpls

and refsOfILMethodImpl s m =
    refsOfILOverridesSpec s m.Overrides
    refsOfILMethodSpec s m.OverrideBy

and refsOfILTypeDef s (td: ILTypeDef) =
    refsOfILTypeDefs s td.NestedTypes
    refsOfILGenericParams s td.GenericParams
    refsOfILTypes s (td.Implements.Value |> List.map _.Type)
    Option.iter (refsOfILType s) td.Extends.Value
    refsOfILMethodDefs s td.Methods
    refsOfILFieldDefs s (td.Fields.AsList())
    refsOfILMethodImpls s (td.MethodImpls.AsList())
    refsOfILEventDefs s td.Events
    refsOfILCustomAttrs s td.CustomAttrs
    refsOfILPropertyDefs s td.Properties

and refsOfILTypeDefs s (types: ILTypeDefs) = Seq.iter (refsOfILTypeDef s) types

and refsOfILExportedType s (c: ILExportedTypeOrForwarder) = refsOfILCustomAttrs s c.CustomAttrs

and refsOfILExportedTypes s (tab: ILExportedTypesAndForwarders) =
    List.iter (refsOfILExportedType s) (tab.AsList())

and refsOfILResourceLocation s x =
    match x with
    | ILResourceLocation.Local _ -> ()
    | ILResourceLocation.File(mref, _) -> refsOfILModuleRef s mref
    | ILResourceLocation.Assembly aref -> refsOfILAssemblyRef s aref

and refsOfILResource s x =
    refsOfILResourceLocation s x.Location
    refsOfILCustomAttrs s x.CustomAttrs

and refsOfILResources s (tab: ILResources) =
    List.iter (refsOfILResource s) (tab.AsList())

and refsOfILModule s m =
    refsOfILTypeDefs s m.TypeDefs
    refsOfILResources s m.Resources
    refsOfILCustomAttrs s m.CustomAttrs
    Option.iter (refsOfILManifest s) m.Manifest

and refsOfILManifest s (m: ILAssemblyManifest) =
    refsOfILCustomAttrs s m.CustomAttrs
    refsOfILExportedTypes s m.ExportedTypes

let computeILRefs ilg modul =
    let s =
        {
            ilg = ilg
            refsA = HashSet<_>(HashIdentity.Structural)
            refsM = HashSet<_>(HashIdentity.Structural)
            refsTs = HashSet<_>(HashIdentity.Structural)
            refsMs = HashSet<_>(HashIdentity.Structural)
            refsFs = HashSet<_>(HashIdentity.Structural)
        }

    refsOfILModule s modul

    {
        AssemblyReferences = s.refsA.ToArray()
        ModuleReferences = s.refsM.ToArray()
        TypeReferences = s.refsTs.ToArray()
        MethodReferences = s.refsMs.ToArray()
        FieldReferences = s.refsFs.ToArray()
    }

let unscopeILTypeRef (x: ILTypeRef) =
    ILTypeRef.Create(ILScopeRef.Local, x.Enclosing, x.Name)

let rec unscopeILTypeSpec (tspec: ILTypeSpec) =
    let tref = tspec.TypeRef
    let tinst = tspec.GenericArgs
    let tref = unscopeILTypeRef tref
    ILTypeSpec.Create(tref, unscopeILTypes tinst)

and unscopeILType ty =
    match ty with
    | ILType.Ptr t -> ILType.Ptr(unscopeILType t)
    | ILType.FunctionPointer t -> ILType.FunctionPointer(unscopeILCallSig t)
    | ILType.Byref t -> ILType.Byref(unscopeILType t)
    | ILType.Boxed cr -> mkILBoxedType (unscopeILTypeSpec cr)
    | ILType.Array(s, ty) -> ILType.Array(s, unscopeILType ty)
    | ILType.Value cr -> ILType.Value(unscopeILTypeSpec cr)
    | ILType.Modified(b, tref, ty) -> ILType.Modified(b, unscopeILTypeRef tref, unscopeILType ty)
    | x -> x

and unscopeILTypes i =
    if List.isEmpty i then i else List.map unscopeILType i

and unscopeILCallSig csig =
    mkILCallSig (csig.CallingConv, unscopeILTypes csig.ArgTypes, unscopeILType csig.ReturnType)

let resolveILMethodRefWithRescope r (td: ILTypeDef) (mref: ILMethodRef) =
    let args = mref.ArgTypes
    let nargs = args.Length
    let nm = mref.Name
    let possibles = td.Methods.FindByNameAndArity(nm, nargs)

    if isNil possibles then
        failwith ("no method named " + nm + " found in type " + td.Name)

    let argTypes = mref.ArgTypes |> List.map r
    let retType: ILType = r mref.ReturnType

    match
        possibles
        |> List.filter (fun md ->
            mref.CallingConv = md.CallingConv
            && (md.Parameters, argTypes)
               ||> List.lengthsEqAndForall2 (fun p1 p2 -> r p1.Type = p2)
            && md.GenericParams.Length = mref.GenericArity
            &&
            // REVIEW: this uses equality on ILType. For CMOD_OPTIONAL this is not going to be correct
            r md.Return.Type = retType)
    with
    | [] ->
        failwith (
            "no method named "
            + nm
            + " with appropriate argument types found in type "
            + td.Name
        )
    | [ mdef ] -> mdef
    | _ ->
        failwith (
            "multiple methods named "
            + nm
            + " appear with identical argument types in type "
            + td.Name
        )

let resolveILMethodRef td mref =
    resolveILMethodRefWithRescope id td mref

let mkRefToILModule m = ILModuleRef.Create(m.Name, true, None)

type ILEventRef =
    {
        erA: ILTypeRef
        erB: string
    }

    static member Create(a, b) = { erA = a; erB = b }

    member x.DeclaringTypeRef = x.erA

    member x.Name = x.erB

type ILPropertyRef =
    {
        prA: ILTypeRef
        prB: string
    }

    static member Create(a, b) = { prA = a; prB = b }

    member x.DeclaringTypeRef = x.prA

    member x.Name = x.prB
