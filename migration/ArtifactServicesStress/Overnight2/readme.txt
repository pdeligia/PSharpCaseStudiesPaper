From: John Erickson 
Sent: Sunday, September 20, 2015 19:45
To: Matt McCutchen <matt@mattmccutchen.net>
Cc: Pantazis Deligiannis <pdeligia@me.com>; Shaz Qadeer <qadeer@microsoft.com>; Akash Lal <akashl@microsoft.com>; Cheng Huang <Cheng.Huang@microsoft.com>
Subject: RE: any update regarding stress testing Live Migration?

"Hence the failures on those
runs confirm that something is wrong with the baseline."

Thanks for checking that. My guess is that  it has something to do with the polling configuration service. 

Sent from my Windows Phone
________________________________________
From: Matt McCutchen
Sent: ‎9/‎20/‎2015 5:23 PM
To: John Erickson
Cc: Pantazis Deligiannis; Shaz Qadeer; Akash Lal; Cheng Huang
Subject: Re: any update regarding stress testing Live Migration?
Thanks for the data, John.

I wasn't able to open the coverage file using Visual Studio Community
2015, but using a third-party tool
(https://na01.safelinks.protection.outlook.com/?url=https%3a%2f%2fgithub.com%2fjsargiot%2fvisual-coverage&data=01%7c01%7cJohn.Erickson%40microsoft.com%7c897238dbd5f74c6cf35b08d2c21aeed7%7c72f988bf86f141af91ab2d7cd011db47%7c1&sdata=bowfH33xqGpy77mWXsPErElFpchuGhoI5tgvR%2bCeZh8%3d) I was able to convert it
to an XML format that I could at least search for specific source lines.
I confirmed that the MigratingTable implementation of streaming queries
is not being reached, so the runs with optional bugs in this code (bugs
#1-4) enabled are equivalent to baseline.  Hence the failures on those
runs confirm that something is wrong with the baseline.

I'd like to help debug this, but I don't see any way we can get this
fixed and the results into the paper before the deadline.  So I propose
to omit the stress test from the submission.  That means it won't help
strengthen the submission for acceptance, but I would hope to add it in
the final version (if the paper is accepted) or a resubmission (if not),
assuming of course that we allow ourselves enough time.

Re non-actionable failures: the natural thing to do would be to add
logging to the stress test, like that in the P# test.  In fact, the P#
test already has code to log all IChainTable2 calls and results both
from the test harness to the MigratingTable and from the MigratingTable
to its backends.  If I factored this code out into an IChainTable2
wrapper, the stress test could use the same wrapper.  Assuming I
understand correctly that the stress test is using real Azure backends,
the log wouldn't tell us the order in which the calls were actually
processed by the backends, so it may or may not be enough to diagnose
the problem.  We could also try a stress test against my in-memory
table.  We could try a few different things and report on the experience
for the final version or resubmission.

On Sun, 2015-09-20 at 15:30 +0000, John Erickson wrote:
> Added coverage data from a single stress iteration.
> 
>  
> 
> From: John Erickson 
> Sent: Sunday, September 20, 2015 08:21
> To: Pantazis Deligiannis <pdeligia@me.com>
> Cc: Matt McCutchen <matt@mattmccutchen.net>; Shaz Qadeer
> <qadeer@microsoft.com>; Akash Lal <akashl@microsoft.com>; Cheng Huang
> <Cheng.Huang@microsoft.com>
> Subject: RE: any update regarding stress testing Live Migration?
> 
> 
>  
> 
> Added a new batch of results. Make sure your Excel viewer can see the
> comments as that’s where I put the stack trace. Most of the bugs
> caused some sort of failure, but I am a little worried that there
> might be some latent bug in the layers above MTable.  I have the
> control “bugless” runs to try to account for this.
> 
>  
> 
> A key point is that most of the failures are not actionable.  For
> example, “blob deleted” just means a blob was gone before it should
> have been.  Each thread adds a reference, checks to make sure the blob
> hasn’t been deleted, and then removes its reference. “Blob deleted”
> just means that second step failed. Something went wrong, somewhere.
> 
>  
> 
> 
> 
>  
> 
