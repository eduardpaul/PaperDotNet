from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .json_object import JsonObject
    from .workflow_trigger_parameters import WorkflowTriggerParameters

@dataclass
class WorkflowTrigger(AdditionalDataHolder, Parsable):
    """
    When a workflow runs: `type` is `manual` (started by a person), an item event (`itemAdded`,`itemUpdated`, `itemDeleted`, `itemRestored`), `schedule`, `date`, a module trigger(`document.processed`, `approval.decided`, `task.completed`, `comment.added`) or an extensiontrigger. `list` and `contentType` narrow it by name; `changedFields` (updates) needs one of them tochange; `terms` (term paths `Group/Set/Term`) needs the item to have one of them or a term below.Omit `list` to watch any list in the workspace.`schedule` runs on `cron` (5 fields) in `timeZone` (default: the organization's). `date` runs foreach item of `list` when its date `field` plus `offsetHours` (negative: before) is reached. `manual`may describe the `inputs` a person gives when starting it (a JSON Schema object; they become run variables).`data` (module and extension triggers) needs the trigger's data to have these values, e.g. `{ "hasText": false }`.
    """
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The changedFields property
    changed_fields: Optional[list[str]] = None
    # The contentType property
    content_type: Optional[str] = None
    # The cron property
    cron: Optional[str] = None
    # The data property
    data: Optional[JsonObject] = None
    # The field property
    field: Optional[str] = None
    # The inputs property
    inputs: Optional[JsonObject] = None
    # The list property
    list_: Optional[str] = None
    # The offsetHours property
    offset_hours: Optional[float] = None
    # The parameters property
    parameters: Optional[WorkflowTriggerParameters] = None
    # The terms property
    terms: Optional[list[str]] = None
    # The timeZone property
    time_zone: Optional[str] = None
    # The type property
    type: Optional[str] = None

    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> WorkflowTrigger:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: WorkflowTrigger
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return WorkflowTrigger()

    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .json_object import JsonObject
        from .workflow_trigger_parameters import WorkflowTriggerParameters

        from .json_object import JsonObject
        from .workflow_trigger_parameters import WorkflowTriggerParameters

        fields: dict[str, Callable[[Any], None]] = {
            "changedFields": lambda n : setattr(self, 'changed_fields', n.get_collection_of_primitive_values(str)),
            "contentType": lambda n : setattr(self, 'content_type', n.get_str_value()),
            "cron": lambda n : setattr(self, 'cron', n.get_str_value()),
            "data": lambda n : setattr(self, 'data', n.get_object_value(JsonObject)),
            "field": lambda n : setattr(self, 'field', n.get_str_value()),
            "inputs": lambda n : setattr(self, 'inputs', n.get_object_value(JsonObject)),
            "list": lambda n : setattr(self, 'list_', n.get_str_value()),
            "offsetHours": lambda n : setattr(self, 'offset_hours', n.get_float_value()),
            "parameters": lambda n : setattr(self, 'parameters', n.get_object_value(WorkflowTriggerParameters)),
            "terms": lambda n : setattr(self, 'terms', n.get_collection_of_primitive_values(str)),
            "timeZone": lambda n : setattr(self, 'time_zone', n.get_str_value()),
            "type": lambda n : setattr(self, 'type', n.get_str_value()),
        }
        return fields

    def serialize(self,writer: SerializationWriter) -> None:
        """
        Serializes information the current object
        param writer: Serialization writer to use to serialize this model
        Returns: None
        """
        if writer is None:
            raise TypeError("writer cannot be null.")
        writer.write_collection_of_primitive_values("changedFields", self.changed_fields)
        writer.write_str_value("contentType", self.content_type)
        writer.write_str_value("cron", self.cron)
        writer.write_object_value("data", self.data)
        writer.write_str_value("field", self.field)
        writer.write_object_value("inputs", self.inputs)
        writer.write_str_value("list", self.list_)
        writer.write_float_value("offsetHours", self.offset_hours)
        writer.write_object_value("parameters", self.parameters)
        writer.write_collection_of_primitive_values("terms", self.terms)
        writer.write_str_value("timeZone", self.time_zone)
        writer.write_str_value("type", self.type)
        writer.write_additional_data_value(self.additional_data)


