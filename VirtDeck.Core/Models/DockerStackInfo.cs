using System.Collections.Generic;

namespace VirtDeck.Models
{
    /// <summary>
    /// One docker compose project on the host, which is what this app calls a stack.
    ///
    /// <para>A compose project has no id of its own. It exists because compose stamped
    /// <c>com.docker.compose.project</c> on every container it created, so the name in that label
    /// <i>is</i> the identity, and it is the merge key everything downstream uses.</para>
    ///
    /// <para>Two halves fill this in, and either can be the only one. The <b>labels</b> answer for a
    /// project that has containers right now, whoever created it. The <b>stacks root</b> answers for
    /// a project VirtDeck wrote a compose file for, which is the only thing that can describe one
    /// that is fully down: a project with no containers has no labels and is invisible to docker
    /// itself. That is the whole reason <see cref="Services.DockerService.StacksRoot"/> exists.</para>
    /// </summary>
    public class DockerStackInfo
    {
        /// <summary>
        /// The compose project name, from <c>com.docker.compose.project</c> or from the stack
        /// directory's own name. Lowercase by compose's own rule, and the merge key.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// <c>com.docker.compose.project.working_dir</c>, documented upstream as absolute. Empty on a
        /// stack that has never been deployed, where nothing has yet written a label to read.
        /// </summary>
        public string WorkingDir { get; set; } = string.Empty;

        /// <summary>
        /// The compose files this project was built from, absolute, split out of the comma-separated
        /// <c>com.docker.compose.project.config_files</c> label. A stack VirtDeck created has exactly
        /// one; a discovered one can name an override file beside the base.
        /// </summary>
        public IReadOnlyList<string> ConfigFiles { get; set; } = new List<string>();

        /// <summary>
        /// Whether the first of <see cref="ConfigFiles"/> is on the host right now.
        ///
        /// <para>Not a formality: the labels record where a project's files <i>were</i> when it was
        /// brought up, and a checkout can be deleted out from under a running stack. Without the
        /// file there is nothing to hand <c>compose up</c> or <c>compose down</c>, so this is what
        /// those two are gated on rather than a guess. Start and stop still work, because they act
        /// on the containers.</para>
        /// </summary>
        public bool ConfigPresent { get; set; }

        /// <summary>
        /// Whether this stack's compose file lives under <see cref="Services.DockerService.StacksRoot"/>,
        /// which is what makes it VirtDeck's to edit and delete.
        ///
        /// <para>Read off the path, never off which half of the listing produced the record, so a
        /// stack in the root that somebody brought up by hand from a terminal is still correctly
        /// ours.</para>
        /// </summary>
        public bool Managed { get; set; }

        /// <summary>
        /// The project's containers, whatever state they are in. One-off containers from
        /// <c>compose run</c> are not here: they carry the project label but are not part of the
        /// stack, and counting them would put a phantom service in the table.
        /// </summary>
        public IReadOnlyList<DockerStackMember> Members { get; set; } = new List<DockerStackMember>();

        /// <summary>
        /// Whether <c>docker ps</c> could be asked at all, the same three-state guard the networks
        /// and images listings carry. False makes an empty <see cref="Members"/> mean "nobody could
        /// look" rather than "nothing is running", which are different answers and only one of them
        /// is worth acting on.
        /// </summary>
        public bool MembersKnown { get; set; }
    }

    /// <summary>
    /// One container of a stack, carried by id so no command has to address it by name, plus the
    /// compose service it is an instance of and the state docker reports for it.
    /// </summary>
    public sealed record DockerStackMember(string Id, string Name, string Service, string State);
}
