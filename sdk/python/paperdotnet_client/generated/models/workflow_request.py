from __future__ import annotations
from collections.abc import Callable
from dataclasses import dataclass, field
from kiota_abstractions.serialization import AdditionalDataHolder, Parsable, ParseNode, SerializationWriter
from typing import Any, Optional, TYPE_CHECKING, Union

if TYPE_CHECKING:
    from .workflow_step import WorkflowStep

@dataclass
class WorkflowRequest(AdditionalDataHolder, Parsable):
    # Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.
    additional_data: dict[str, Any] = field(default_factory=dict)

    # The enabled property
    enabled: Optional[bool] = True
    # The description property
    description: Optional[str] = None
    # The name property
    name: Optional[str] = None
    # The steps property
    steps: Optional[list[WorkflowStep]] = None
    
    @staticmethod
    def create_from_discriminator_value(parse_node: ParseNode) -> WorkflowRequest:
        """
        Creates a new instance of the appropriate class based on discriminator value
        param parse_node: The parse node to use to read the discriminator value and create the object
        Returns: WorkflowRequest
        """
        if parse_node is None:
            raise TypeError("parse_node cannot be null.")
        return WorkflowRequest()
    
    def get_field_deserializers(self,) -> dict[str, Callable[[ParseNode], None]]:
        """
        The deserialization information for the current model
        Returns: dict[str, Callable[[ParseNode], None]]
        """
        from .workflow_step import WorkflowStep

        from .workflow_step import WorkflowStep

        fields: dict[str, Callable[[Any], None]] = {
            "description": lambda n : setattr(self, 'description', n.get_str_value()),
            "enabled": lambda n : setattr(self, 'enabled', n.get_bool_value()),
            "name": lambda n : setattr(self, 'name', n.get_str_value()),
            "steps": lambda n : setattr(self, 'steps', n.get_collection_of_object_values(WorkflowStep)),
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
        writer.write_str_value("description", self.description)
        writer.write_bool_value("enabled", self.enabled)
        writer.write_str_value("name", self.name)
        writer.write_collection_of_object_values("steps", self.steps)
        writer.write_additional_data_value(self.additional_data)
    

