ImplFile
  (ParsedImplFileInput
     ("/root/SynType/Named 09.fs", false, QualifiedNameOfFile Module, [], [],
      [SynModuleOrNamespace
         ([Module], false, NamedModule,
          [Expr
             (Match
                (Yes (3,0--3,12), Const (Int32 1, (3,6--3,7)),
                 [SynMatchClause
                    (Named
                       (SynIdent (x, None), false, Some (Private (4,2--4,9)),
                        (4,2--4,11)), None, Const (Unit, (4,15--4,17)),
                     (4,2--4,17), Yes, { ArrowRange = Some (4,12--4,14)
                                         BarRange = Some (4,0--4,1) })],
                 (3,0--4,17), { MatchKeyword = (3,0--3,5)
                                WithKeyword = (3,8--3,12) }), (3,0--4,17))],
          PreXmlDoc ((1,0), FSharp.Compiler.Xml.XmlDocCollector), [], None,
          (1,0--4,17), { LeadingKeyword = Module (1,0--1,6) })], (true, true),
      { ConditionalDirectives = []
        CodeComments = [] }, set []))
